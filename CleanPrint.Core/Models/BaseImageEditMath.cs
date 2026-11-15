using System;

namespace CleanPrint.Core.Models;

/// <summary>A rectangle of whole pixels: its top-left corner and its size.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// The arithmetic of editing a page's base image — turning it by an angle (the canvas grows so nothing is cut off, the new corners are white) and then cropping it.
/// Everything here is free of files and UI so it can be tested; the editor and the code that moves annotations along with the edit both use it, so the picture and
/// the annotations on it can't disagree about where things went.
///
/// Coordinates: PIXELS, continuous, with the top-left corner of the top-left pixel at (0,0) (so pixel (i,j) covers [i,i+1) x [j,j+1) and the image's centre is
/// (width/2, height/2)). Angles are in degrees, CLOCKWISE as seen on screen — the same sense as an annotation's RotationDegrees and WPF's RotateTransform.
/// </summary>
public static class BaseImageEditMath
{
    /// <summary>An angle brought into (-180, 180].</summary>
    public static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        if (a <= -180.0) a += 360.0;
        else if (a > 180.0) a -= 360.0;
        return a;
    }

    /// <summary>True if the angle is (within a hair of) a whole number of quarter turns; <paramref name="quarterTurnsClockwise"/> is then 0, 1, 2 or 3.</summary>
    public static bool IsRightAngle(double degrees, out int quarterTurnsClockwise)
    {
        double quarters = NormalizeAngle(degrees) / 90.0;
        double nearest = Math.Round(quarters);
        quarterTurnsClockwise = 0;
        if (Math.Abs(quarters - nearest) > 1e-9) return false;
        quarterTurnsClockwise = (((int)nearest % 4) + 4) % 4;
        return true;
    }

    /// <summary>The size of the canvas that just holds the image turned by <paramref name="degrees"/>. A quarter turn is exact (no extra pixel); any other angle rounds up.</summary>
    public static (int Width, int Height) RotatedCanvasSize(int width, int height, double degrees)
    {
        if (IsRightAngle(degrees, out int quarters))
            return quarters % 2 == 0 ? (width, height) : (height, width);

        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        return ((int)Math.Ceiling(width * cos + height * sin - 1e-9), (int)Math.Ceiling(width * sin + height * cos - 1e-9));
    }

    /// <summary>Where a point of the original image (continuous pixel coordinates) lands on the turned canvas: turned about the image's centre, which goes to the
    /// canvas's centre.</summary>
    public static GeoPoint MapPixel(GeoPoint p, int oldWidth, int oldHeight, int newWidth, int newHeight, double degrees)
    {
        var turned = TextGeometry.Rotate(new GeoPoint(p.X - oldWidth / 2.0, p.Y - oldHeight / 2.0), degrees);
        return new GeoPoint(turned.X + newWidth / 2.0, turned.Y + newHeight / 2.0);
    }

    /// <summary>
    /// The 2x3 matrix (row-major: a, b, tx, c, d, ty) that OpenCV's WarpAffine wants to produce the turned canvas from the original: it maps source pixel coordinates to
    /// destination pixel coordinates. OpenCV numbers pixels by their CENTRES (pixel i is at i), not by their corner as <see cref="MapPixel"/> does, so the half-pixel
    /// difference is folded into the translation — which is what makes the image's centre land on the canvas's centre exactly.
    /// </summary>
    public static double[] WarpMatrix(int oldWidth, int oldHeight, int newWidth, int newHeight, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        // q' = R q + t, with t = R((0.5, 0.5) - c) + c' - (0.5, 0.5)
        double hx = 0.5 - oldWidth / 2.0, hy = 0.5 - oldHeight / 2.0;
        double tx = cos * hx - sin * hy + newWidth / 2.0 - 0.5;
        double ty = sin * hx + cos * hy + newHeight / 2.0 - 0.5;
        return new[] { cos, -sin, tx, sin, cos, ty };
    }

    // ------------------------------------------------------------------ growing the canvas
    // The crop is a rectangle of the turned canvas, but it need not stay INSIDE it: a crop that reaches past the edge makes the saved page bigger than the image, the extra being white.
    // That is how the canvas is enlarged. These keep it sensible.

    /// <summary>The most pixels a saved page may have: 60 megapixels, or the original page's own size if that was already bigger. Every full-resolution step holds several copies in memory.</summary>
    public static long MaxOutputPixels(int originalWidth, int originalHeight) => Math.Max(60_000_000L, (long)originalWidth * originalHeight);

    /// <summary>How far past each edge of the turned canvas the crop may reach: as much again as the canvas's longer side.</summary>
    public static int MaxReach(int canvasWidth, int canvasHeight) => Math.Max(canvasWidth, canvasHeight);

    /// <summary>
    /// Brings a crop into what is allowed: at least 1x1 pixel, no further than <see cref="MaxReach"/> past any edge of the canvas, and no more than <paramref name="maxPixels"/> in all
    /// (if it is, both sides are shrunk in proportion, keeping the top-left corner).
    /// </summary>
    public static PixelRect ClampCrop(PixelRect crop, int canvasWidth, int canvasHeight, long maxPixels)
    {
        int reach = MaxReach(canvasWidth, canvasHeight);
        int x = Math.Clamp(crop.X, -reach, canvasWidth + reach - 1), y = Math.Clamp(crop.Y, -reach, canvasHeight + reach - 1);
        int width = Math.Clamp(crop.Width, 1, canvasWidth + reach - x), height = Math.Clamp(crop.Height, 1, canvasHeight + reach - y);

        if ((long)width * height > maxPixels)
        {
            double shrink = Math.Sqrt((double)maxPixels / ((double)width * height));
            width = Math.Max(1, (int)Math.Floor(width * shrink));
            height = Math.Max(1, (int)Math.Floor(height * shrink));
        }
        return new PixelRect(x, y, width, height);
    }

    /// <summary>The grey margin shown around the canvas so the crop can be dragged out past its edge: a fifth of the longer side (at least 64 pixels) all round.</summary>
    public static PixelRect Pasteboard(int canvasWidth, int canvasHeight)
    {
        int margin = Math.Max(64, Math.Max(canvasWidth, canvasHeight) / 5);
        return new PixelRect(-margin, -margin, canvasWidth + 2 * margin, canvasHeight + 2 * margin);
    }

    /// <summary>What the editor draws: the canvas with its pasteboard, and — if the crop was typed in reaching further than that — the crop too.</summary>
    public static PixelRect StageRect(int canvasWidth, int canvasHeight, PixelRect crop)
    {
        var board = Pasteboard(canvasWidth, canvasHeight);
        int left = Math.Min(board.X, crop.X), top = Math.Min(board.Y, crop.Y);
        int right = Math.Max(board.Right, crop.Right), bottom = Math.Max(board.Bottom, crop.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// How to cut a crop out of an image the crop may reach beyond: the part of the SOURCE it covers, and where that part lands in the new page (the rest of the new page is filled
    /// white). Null if the crop misses the image altogether.
    /// </summary>
    public static (PixelRect Source, int DestinationX, int DestinationY)? CropPlan(PixelRect crop, int sourceWidth, int sourceHeight)
    {
        int left = Math.Max(crop.X, 0), top = Math.Max(crop.Y, 0);
        int right = Math.Min(crop.Right, sourceWidth), bottom = Math.Min(crop.Bottom, sourceHeight);
        if (right <= left || bottom <= top) return null;
        return (new PixelRect(left, top, right - left, bottom - top), left - crop.X, top - crop.Y);
    }

    /// <summary>
    /// How many pixels on its longer side the editor's preview copy of the page should have for the zoom it is at, so a zoomed-in view isn't a blown-up thumbnail: the smallest of 1600, 3200,
    /// 6400 pixels that is at least as many as the screen is showing, or the whole page once it needs more than that. <paramref name="shownLongestPixels"/> is how many screen pixels the
    /// page's longer side takes at this zoom.
    /// </summary>
    public static int PreviewDimension(double shownLongestPixels, int pageLongestPixels)
    {
        foreach (int tier in new[] { 1600, 3200, 6400 })
            if (shownLongestPixels <= tier) return Math.Min(tier, pageLongestPixels);
        return pageLongestPixels;
    }
}

/// <summary>
/// How the annotations on a page follow an edit of its base image (skew-corrected, turned by an angle, then cropped). Turning and cropping are a rigid motion — a turn about the centre and
/// a shift — so a text box or a picture goes exactly where the part of the page it sat on went: its anchor is carried across and its rotation turns with the page. Polygons and curves
/// carry every point across. The one thing that cannot follow exactly is an axis-aligned ellipse turned by a angle that is not a half turn: it keeps its centre and radii (and swaps
/// them for a quarter turn).
///
/// A SKEW CORRECTION is not rigid: it stretches the page to a rectangle. Points go through its homography, so a polygon or curve follows exactly (a straight edge stays straight). A text box
/// or picture can't be stretched, so it keeps its size and its anchor is carried across; its rotation takes up the local direction the page's own lines were turned to.
///
/// Annotation coordinates are in page units (96 per inch) measured from the top-left of what the page showed AT THE TIME — which, when auto-crop is on, is the cropped
/// page, not the whole image. <c>oldOffsetPx</c> and <c>newOffsetPx</c> are that auto-crop shift (in image pixels) before and after the edit ((0,0) when auto-crop is off
/// or for the Before view), so a point is first put back on the whole old image, carried across, then expressed relative to whatever the new page will show.
/// </summary>
public sealed class AnnotationEditTransform
{
    private readonly int _baseWidth, _baseHeight, _newWidth, _newHeight;   // the image the turn starts from (after any skew correction), and the turned canvas
    private readonly double _angle, _pixelsPerUnit;
    private readonly PixelRect _crop;
    private readonly GeoPoint _oldOffset, _newOffset;
    private readonly PerspectiveStage? _skew;

    public AnnotationEditTransform(int oldWidth, int oldHeight, double angleDegrees, PixelRect crop, double dpi, GeoPoint oldOffsetPx, GeoPoint newOffsetPx,
        PerspectiveStage? skew = null)
    {
        _skew = skew;
        _baseWidth = skew?.Width ?? oldWidth;
        _baseHeight = skew?.Height ?? oldHeight;
        _angle = BaseImageEditMath.NormalizeAngle(angleDegrees);
        (_newWidth, _newHeight) = BaseImageEditMath.RotatedCanvasSize(_baseWidth, _baseHeight, _angle);
        _crop = crop;
        _pixelsPerUnit = dpi / 96.0;
        _oldOffset = oldOffsetPx;
        _newOffset = newOffsetPx;
    }

    /// <summary>A point (page units, as the old page showed it) in pixels of the image the turn starts from: put back on the whole old image, then through the skew correction if there is one.</summary>
    private GeoPoint ToBasePixel(GeoPoint point)
    {
        var oldPixel = new GeoPoint(point.X * _pixelsPerUnit + _oldOffset.X, point.Y * _pixelsPerUnit + _oldOffset.Y);
        return _skew is null ? oldPixel : PerspectiveMath.Apply(_skew.Homography, oldPixel);
    }

    /// <summary>Where a point (page units, as the old page showed it) is on the new page.</summary>
    public GeoPoint Map(GeoPoint point)
    {
        var turned = BaseImageEditMath.MapPixel(ToBasePixel(point), _baseWidth, _baseHeight, _newWidth, _newHeight, _angle);
        return new GeoPoint((turned.X - _crop.X - _newOffset.X) / _pixelsPerUnit, (turned.Y - _crop.Y - _newOffset.Y) / _pixelsPerUnit);
    }

    /// <summary>Moves one annotation to where its part of the page went. Nothing is ever dropped, even if the crop now leaves it off the page.</summary>
    public void ApplyTo(PolygonAnnotation annotation)
    {
        if (annotation.Vertices.Count == 0) return;

        switch (annotation.Kind)
        {
            case ShapeKind.Text:
            case ShapeKind.Image:
            {
                // A box hung from its top-left anchor and turned about it: a rigid motion carries the anchor, and the box turns with the page.
                var anchor = new GeoPoint(annotation.Vertices[0].X, annotation.Vertices[0].Y);
                double rotation = annotation.RotationDegrees;
                if (_skew is not null)
                {
                    // The page's own lines have been turned to whatever the correction made of them: follow where the box's top edge now points.
                    double radians = annotation.RotationDegrees * Math.PI / 180.0;
                    const double probe = 100;
                    var tip = new GeoPoint(anchor.X + Math.Cos(radians) * probe, anchor.Y + Math.Sin(radians) * probe);
                    var from = ToBasePixel(anchor); var to = ToBasePixel(tip);
                    rotation = Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI;
                }
                Replace(annotation, 0, Map(anchor));
                annotation.RotationDegrees = BaseImageEditMath.NormalizeAngle(rotation + _angle);
                break;
            }

            case ShapeKind.Ellipse when annotation.Vertices.Count >= 3:
            {
                // [centre, right-hand extent, bottom extent], radii along the axes. Keep the radii; a quarter turn swaps them.
                var centre = annotation.Vertices[0];
                double radiusX = Math.Abs(annotation.Vertices[1].X - centre.X), radiusY = Math.Abs(annotation.Vertices[2].Y - centre.Y);
                if (Math.Round(_angle / 90.0) % 2 != 0) (radiusX, radiusY) = (radiusY, radiusX);
                var moved = Map(new GeoPoint(centre.X, centre.Y));
                annotation.Vertices[0] = new PolygonPoint { X = moved.X, Y = moved.Y };
                annotation.Vertices[1] = new PolygonPoint { X = moved.X + radiusX, Y = moved.Y };
                annotation.Vertices[2] = new PolygonPoint { X = moved.X, Y = moved.Y + radiusY };
                break;
            }

            default:
                // Polygons, rectangles, curves, circles, arcs — and Freeze/Hide bands — are defined by their points.
                for (int i = 0; i < annotation.Vertices.Count; i++)
                    Replace(annotation, i, Map(new GeoPoint(annotation.Vertices[i].X, annotation.Vertices[i].Y)));
                break;
        }
    }

    private static void Replace(PolygonAnnotation annotation, int index, GeoPoint to) =>
        annotation.Vertices[index] = new PolygonPoint { X = to.X, Y = to.Y };
}
