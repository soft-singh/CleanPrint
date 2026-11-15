using System;

namespace CleanPrint.Core.Models;

/// <summary>
/// A four-sided shape on an image, given by its corners in the order top-left, top-right, bottom-right, bottom-left — as the shape of a page appears in a photograph taken at an angle.
/// Coordinates are continuous pixels, with the top-left corner of the top-left pixel at (0,0).
/// </summary>
public readonly record struct Quad(GeoPoint TopLeft, GeoPoint TopRight, GeoPoint BottomRight, GeoPoint BottomLeft)
{
    public GeoPoint[] Points => new[] { TopLeft, TopRight, BottomRight, BottomLeft };

    /// <summary>The same shape scaled about the origin — for working on a reduced copy of the image.</summary>
    public Quad Scaled(double factor) => new(
        new GeoPoint(TopLeft.X * factor, TopLeft.Y * factor), new GeoPoint(TopRight.X * factor, TopRight.Y * factor),
        new GeoPoint(BottomRight.X * factor, BottomRight.Y * factor), new GeoPoint(BottomLeft.X * factor, BottomLeft.Y * factor));

    /// <summary>A rectangle a fraction in from every edge of an image — where the corners start when skew correction is begun.</summary>
    public static Quad Inset(int imageWidth, int imageHeight, double fraction)
    {
        double x = imageWidth * fraction, y = imageHeight * fraction;
        return new Quad(new GeoPoint(x, y), new GeoPoint(imageWidth - x, y), new GeoPoint(imageWidth - x, imageHeight - y), new GeoPoint(x, imageHeight - y));
    }
}

/// <summary>
/// Skew correction as a perspective warp: the four-sided shape the page has in the photo is stretched to a true rectangle. This is the arithmetic — free of files and UI so it can be
/// tested — shared by the code that warps the pixels and the code that moves annotations along with them.
///
/// A homography is a 3x3 matrix, held here as nine numbers row by row. It maps a point (x, y) to (u, v) = ((h0 x + h1 y + h2) / w, (h3 x + h4 y + h5) / w) with w = h6 x + h7 y + h8.
/// </summary>
public static class PerspectiveMath
{
    private static double Distance(GeoPoint a, GeoPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The size of the corrected page: as wide as the longer of the top and bottom edges, as tall as the longer of the left and right edges — so no part of the photo is squeezed
    /// below the resolution it was shot at.
    /// </summary>
    public static (int Width, int Height) OutputSize(Quad q) => (
        Math.Max(1, (int)Math.Round(Math.Max(Distance(q.TopLeft, q.TopRight), Distance(q.BottomLeft, q.BottomRight)))),
        Math.Max(1, (int)Math.Round(Math.Max(Distance(q.TopLeft, q.BottomLeft), Distance(q.TopRight, q.BottomRight)))));

    /// <summary>
    /// The homography that carries the quad's corners to the corners of a <paramref name="width"/> x <paramref name="height"/> rectangle at the origin (top-left to (0,0), top-right to
    /// (width,0), bottom-right to (width,height), bottom-left to (0,height)). Solved from those four correspondences — eight equations, eight unknowns. Throws if the corners are
    /// degenerate (three in a line, or two the same), where no such warp exists.
    /// </summary>
    public static double[] Homography(Quad quad, int width, int height)
    {
        var from = quad.Points;
        var to = new[] { new GeoPoint(0, 0), new GeoPoint(width, 0), new GeoPoint(width, height), new GeoPoint(0, height) };

        var m = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            int r = 2 * i;
            m[r, 0] = x; m[r, 1] = y; m[r, 2] = 1; m[r, 3] = 0; m[r, 4] = 0; m[r, 5] = 0; m[r, 6] = -u * x; m[r, 7] = -u * y; m[r, 8] = u;
            m[r + 1, 0] = 0; m[r + 1, 1] = 0; m[r + 1, 2] = 0; m[r + 1, 3] = x; m[r + 1, 4] = y; m[r + 1, 5] = 1; m[r + 1, 6] = -v * x; m[r + 1, 7] = -v * y; m[r + 1, 8] = v;
        }

        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 8; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-12) throw new ArgumentException("The four corners are degenerate: no perspective correction can be made from them.");
            if (pivot != col)
                for (int k = 0; k < 9; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (int row = 0; row < 8; row++)
            {
                if (row == col) continue;
                double factor = m[row, col] / m[col, col];
                if (factor == 0) continue;
                for (int k = col; k < 9; k++) m[row, k] -= factor * m[col, k];
            }
        }
        var h = new double[9];
        for (int i = 0; i < 8; i++) h[i] = m[i, 8] / m[i, i];
        h[8] = 1;
        return h;
    }

    /// <summary>Where a point goes under the homography.</summary>
    public static GeoPoint Apply(double[] h, GeoPoint p)
    {
        double w = h[6] * p.X + h[7] * p.Y + h[8];
        return new GeoPoint((h[0] * p.X + h[1] * p.Y + h[2]) / w, (h[3] * p.X + h[4] * p.Y + h[5]) / w);
    }

    /// <summary>The homography that undoes <paramref name="h"/>.</summary>
    public static double[] Invert(double[] h)
    {
        double a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], i = h[7], j = h[8];
        double det = a * (e * j - f * i) - b * (d * j - f * g) + c * (d * i - e * g);
        if (Math.Abs(det) < 1e-18) throw new ArgumentException("The matrix cannot be inverted.");
        return new[]
        {
            (e * j - f * i) / det, (c * i - b * j) / det, (b * f - c * e) / det,
            (f * g - d * j) / det, (a * j - c * g) / det, (c * d - a * f) / det,
            (d * i - e * g) / det, (b * g - a * i) / det, (a * e - b * d) / det,
        };
    }

    /// <summary>
    /// The same homography for software that numbers pixels by their CENTRES (OpenCV: pixel i is at i) rather than by their corner as this class does (pixel i spans [i, i+1)): a point is
    /// shifted half a pixel in, mapped, and shifted half a pixel back. Without this a warped image would sit half a pixel off from the annotations moved to follow it.
    /// </summary>
    public static double[] ToPixelCentreConvention(double[] h)
    {
        // result = T(-0.5) * H * T(+0.5)
        double[] Multiply(double[] p, double[] q)
        {
            var r = new double[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    r[row * 3 + col] = p[row * 3] * q[col] + p[row * 3 + 1] * q[3 + col] + p[row * 3 + 2] * q[6 + col];
            return r;
        }
        var plus = new[] { 1.0, 0, 0.5, 0, 1, 0.5, 0, 0, 1 };
        var minus = new[] { 1.0, 0, -0.5, 0, 1, -0.5, 0, 0, 1 };
        return Multiply(minus, Multiply(h, plus));
    }

    /// <summary>True if the four corners make a proper four-sided shape: no side crossing another, no corner folded back, not squashed flat. A warp from anything else would turn the page inside out.</summary>
    public static bool IsConvex(Quad q)
    {
        var p = q.Points;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = p[i]; var b = p[(i + 1) % 4]; var c = p[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) return false;
            if (sign == 0) sign = Math.Sign(cross);
            else if (Math.Sign(cross) != sign) return false;
        }
        return true;
    }

    /// <summary>
    /// How many degrees clockwise to turn a picture so that a line drawn on it from <paramref name="a"/> to <paramref name="b"/> comes out level — or, if it is nearer to upright, upright. (A line's
    /// direction is ambiguous by half a turn, so whichever way it was drawn gives the same answer.) The way a person straightens a crooked scan in GIMP: draw along an edge that should be straight.
    /// </summary>
    public static double StraightenAngle(GeoPoint a, GeoPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 1e-9) return 0;
        double direction = Math.Atan2(dy, dx) * 180.0 / Math.PI;   // clockwise from "rightwards", since y runs down the screen
        while (direction > 90) direction -= 180;
        while (direction <= -90) direction += 180;
        if (Math.Abs(direction) <= 45) return -direction;           // turn it level
        return direction > 0 ? 90 - direction : -90 - direction;    // turn it upright
    }
}

/// <summary>A skew correction ready to use: the quad it was made from, the size of the corrected page, and the homography (in this library's corner convention) that does it.</summary>
public sealed class PerspectiveStage
{
    public Quad Source { get; }
    public int Width { get; }
    public int Height { get; }
    public double[] Homography { get; }

    private PerspectiveStage(Quad source, int width, int height, double[] homography)
    {
        Source = source; Width = width; Height = height; Homography = homography;
    }

    public static PerspectiveStage From(Quad quad)
    {
        var (width, height) = PerspectiveMath.OutputSize(quad);
        return new PerspectiveStage(quad, width, height, PerspectiveMath.Homography(quad, width, height));
    }
}
