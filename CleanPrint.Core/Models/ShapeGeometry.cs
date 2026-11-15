using System;
using System.Collections.Generic;

namespace CleanPrint.Core.Models;

/// <summary>A 2D point, in whatever coordinate space the caller is working in.</summary>
public readonly record struct GeoPoint(double X, double Y);

/// <summary>
/// Turns a shape's few CONTROL points into the polyline that is actually drawn, printed and hit-tested.
///
/// Why flatten at all: the overlay draws <c>Polyline</c> elements, OpenCV prints with <c>Polylines</c>, and
/// hit-testing measures distance to segments — all of which want straight segments. The point of a "true
/// curve" shape is not that the renderer understands curves, but that the shape is DEFINED by its curve (a
/// circle is a centre and a radius, an arc is three points) and is sampled from that definition finely enough
/// that no facet is ever visible. <paramref name="tolerance"/> is that "finely enough": the largest distance, in
/// the caller's own units, the polyline is allowed to stray from the true curve. Callers therefore flatten in the
/// space they draw in — screen pixels for the overlay (so a circle stays smooth however far you zoom in), page
/// pixels for printing — rather than flattening once and scaling the result.
///
/// The control points of each kind are documented on <see cref="ShapeKind"/>.
/// </summary>
public static class ShapeGeometry
{
    private const double MinTolerance = 0.01;

    /// <summary>True for the kinds whose number of control points is fixed by the kind itself, so adding or
    /// deleting a point makes no sense (a circle through three points can't have four). Polygons and curves
    /// are the free-count kinds.</summary>
    public static bool HasFixedPointCount(ShapeKind kind) =>
        kind is ShapeKind.Ellipse or ShapeKind.CircleCenter or ShapeKind.Circle3 or ShapeKind.Arc3 or ShapeKind.Text or ShapeKind.Image;

    /// <summary>
    /// The polyline for a shape. For closed shapes (full circles/ellipses, and a closed Curve) the result does
    /// NOT repeat its first point at the end — closing the loop is the caller's job, exactly as it already is
    /// for polygons. Open results (arcs, open curves) include both end points. Kinds that aren't curves
    /// (Polygon, Text) return their control points unchanged.
    /// </summary>
    public static List<GeoPoint> Flatten(ShapeKind kind, IReadOnlyList<GeoPoint> control, bool closed, double tolerance)
    {
        double tol = Math.Max(tolerance, MinTolerance);
        return kind switch
        {
            ShapeKind.Ellipse => FlattenEllipse(control, tol),
            ShapeKind.CircleCenter => FlattenCircleCenter(control, tol),
            ShapeKind.Circle3 => FlattenCircle3(control, tol),
            ShapeKind.Arc3 => FlattenArc3(control, tol),
            ShapeKind.Curve => FlattenCurve(control, closed, tol),
            _ => new List<GeoPoint>(control),
        };
    }

    // ---------------------------------------------------------------- ellipse / circles / arc

    private static List<GeoPoint> FlattenEllipse(IReadOnlyList<GeoPoint> c, double tol)
    {
        if (c.Count < 3) return new List<GeoPoint>(c);
        double cx = c[0].X, cy = c[0].Y;
        double rx = Math.Abs(c[1].X - cx), ry = Math.Abs(c[2].Y - cy);
        return Ring(cx, cy, rx, ry, tol);
    }

    private static List<GeoPoint> FlattenCircleCenter(IReadOnlyList<GeoPoint> c, double tol)
    {
        if (c.Count < 2) return new List<GeoPoint>(c);
        double r = Distance(c[0], c[1]);
        return Ring(c[0].X, c[0].Y, r, r, tol);
    }

    private static List<GeoPoint> FlattenCircle3(IReadOnlyList<GeoPoint> c, double tol)
    {
        // Three points that don't define a circle (coincident or in a straight line) fall back to just
        // joining them, so the shape stays visible and editable while being dragged through that state.
        if (c.Count < 3 || !TryCircumcircle(c[0], c[1], c[2], out double cx, out double cy, out double r))
            return new List<GeoPoint>(c);
        return Ring(cx, cy, r, r, tol);
    }

    private static List<GeoPoint> FlattenArc3(IReadOnlyList<GeoPoint> c, double tol)
    {
        if (c.Count < 3 || !TryCircumcircle(c[0], c[1], c[2], out double cx, out double cy, out double r))
            return new List<GeoPoint>(c);

        GeoPoint p1 = c[0], p2 = c[1], p3 = c[2];
        double a1 = Math.Atan2(p1.Y - cy, p1.X - cx);
        double a3 = Math.Atan2(p3.Y - cy, p3.X - cx);

        // Which way round the circle goes from p1 to p3 so that it passes THROUGH p2: three points taken in
        // order on a circle turn the same way all the way round, so the turn direction at p2 is the sweep
        // direction (positive cross product = angles increasing).
        double cross = (p2.X - p1.X) * (p3.Y - p2.Y) - (p2.Y - p1.Y) * (p3.X - p2.X);
        double sweep = cross > 0 ? PositiveAngle(a3 - a1) : -PositiveAngle(a1 - a3);

        int n = SegmentsFor(r, Math.Abs(sweep), tol, minimum: 4);
        var points = new List<GeoPoint>(n + 1) { p1 }; // end points exact, not recomputed
        for (int i = 1; i < n; i++)
        {
            double angle = a1 + sweep * i / n;
            points.Add(new GeoPoint(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
        }
        points.Add(p3);
        return points;
    }

    private static List<GeoPoint> Ring(double cx, double cy, double rx, double ry, double tol)
    {
        int n = SegmentsFor(Math.Max(rx, ry), 2 * Math.PI, tol, minimum: 8);
        var points = new List<GeoPoint>(n);
        for (int i = 0; i < n; i++)
        {
            double angle = 2 * Math.PI * i / n;
            points.Add(new GeoPoint(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>How many straight segments approximate an arc of the given radius and sweep so the polyline stays
    /// within <paramref name="tol"/> of the true curve. A chord spanning angle t deviates by r(1 - cos(t/2)), so
    /// the largest allowed step is t = 2 acos(1 - tol/r).</summary>
    private static int SegmentsFor(double radius, double sweep, double tol, int minimum)
    {
        if (radius <= tol) return minimum;
        double step = 2 * Math.Acos(Math.Clamp(1 - tol / radius, -1, 1));
        if (step < 1e-6) return 4000;
        return Math.Clamp((int)Math.Ceiling(sweep / step), minimum, 4000);
    }

    /// <summary>The circle through three points. False when there isn't one (the points are collinear or
    /// coincident) or it's so enormous it might as well be a straight line.</summary>
    private static bool TryCircumcircle(GeoPoint a, GeoPoint b, GeoPoint c, out double cx, out double cy, out double r)
    {
        cx = cy = r = 0;
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-9) return false;

        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        cx = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
        cy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
        r = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));
        return !double.IsNaN(r) && !double.IsInfinity(r) && r < 1e6;
    }

    /// <summary>An angle normalised into [0, 2*pi).</summary>
    private static double PositiveAngle(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    // ---------------------------------------------------------------- smooth curve through points

    /// <summary>
    /// A Catmull-Rom spline through the points — the standard "smooth curve through the points I clicked".
    /// Each span between neighbouring points is converted to the equivalent cubic Bezier (control points at
    /// +-1/6 of the chord between the neighbours on either side), then subdivided until it is flat to
    /// <paramref name="tol"/>. Open curves reuse the end point as its own missing neighbour; a closed curve
    /// wraps around, so the loop has no corner at the join.
    /// </summary>
    private static List<GeoPoint> FlattenCurve(IReadOnlyList<GeoPoint> p, bool closed, double tol)
    {
        int n = p.Count;
        if (n < 3) return new List<GeoPoint>(p); // one or two points: nothing to smooth

        var output = new List<GeoPoint> { p[0] };
        int spans = closed ? n : n - 1;
        for (int i = 0; i < spans; i++)
        {
            GeoPoint p0 = closed ? p[(i - 1 + n) % n] : p[Math.Max(i - 1, 0)];
            GeoPoint p1 = p[i];
            GeoPoint p2 = p[(i + 1) % n];
            GeoPoint p3 = closed ? p[(i + 2) % n] : p[Math.Min(i + 2, n - 1)];

            var c1 = new GeoPoint(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new GeoPoint(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            FlattenCubic(output, p1, c1, c2, p2, tol, 0);
        }

        // The last span of a closed curve ends back on the first point; callers close loops themselves.
        if (closed && output.Count > 1) output.RemoveAt(output.Count - 1);
        return output;
    }

    /// <summary>Adaptive de Casteljau subdivision: emits p3 once both control points lie within tol of the chord.</summary>
    private static void FlattenCubic(List<GeoPoint> output, GeoPoint p0, GeoPoint p1, GeoPoint p2, GeoPoint p3, double tol, int depth)
    {
        if (depth >= 14 || Math.Max(DistanceToLine(p1, p0, p3), DistanceToLine(p2, p0, p3)) <= tol)
        {
            output.Add(p3);
            return;
        }

        GeoPoint p01 = Mid(p0, p1), p12 = Mid(p1, p2), p23 = Mid(p2, p3);
        GeoPoint p012 = Mid(p01, p12), p123 = Mid(p12, p23);
        GeoPoint m = Mid(p012, p123);
        FlattenCubic(output, p0, p01, p012, m, tol, depth + 1);
        FlattenCubic(output, m, p123, p23, p3, tol, depth + 1);
    }

    private static GeoPoint Mid(GeoPoint a, GeoPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance(GeoPoint a, GeoPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Perpendicular distance from p to the infinite line through a and b (to a itself if they coincide).</summary>
    private static double DistanceToLine(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-12) return Distance(p, a);
        return Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / length;
    }
}
