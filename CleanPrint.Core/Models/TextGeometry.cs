using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CleanPrint.Core.Models;

/// <summary>
/// The measurement-free maths behind text annotations: units, line wrapping, and the rotation geometry used for
/// hit-testing, the width handle and the blurred backdrop. Kept free of WPF types (the caller supplies a
/// "how wide is this string" function) so the same code serves the overlay, the print path and the tests.
///
/// A text annotation lives in its own LOCAL frame: origin at its anchor (top-left of the text box), x along the text,
/// y down the page, rotated clockwise by RotationDegrees about the anchor. Hit-testing, for instance, is just
/// "turn the page point into the local frame, then test an axis-aligned rectangle".
/// </summary>
public static class TextGeometry
{
    /// <summary>Device-independent units (1/96 inch) per typographic point (1/72 inch).</summary>
    public const double DiuPerPoint = 96.0 / 72.0;

    public static double PointsToDiu(double points) => points * DiuPerPoint;
    public static double DiuToPoints(double diu) => diu / DiuPerPoint;

    // ------------------------------------------------------------------ wrapping

    /// <summary>One line of wrapped text, and whether it is the last line of its paragraph (the line justified text leaves ragged).</summary>
    public readonly record struct WrappedLine(string Text, bool EndsParagraph);

    /// <summary>
    /// Splits text into display lines. Explicit line breaks always start a new line; when <paramref name="maxWidth"/> is
    /// positive, a paragraph is also wrapped greedily at spaces so that no line is wider than that, and a single word
    /// wider than the box is broken between characters (never inside a surrogate pair, and never between a letter and
    /// the combining mark that belongs to it — vowel signs in Gurmukhi/Devanagari, accents — which would tear the
    /// character apart). <paramref name="maxWidth"/> of 0 or less means "don't wrap".
    /// </summary>
    public static List<string> WrapLines(string? text, double maxWidth, Func<string, double> measure) =>
        WrapLinesDetailed(text, maxWidth, measure).Select(line => line.Text).ToList();

    /// <summary>As <see cref="WrapLines"/>, but each line also says whether it ends its paragraph.</summary>
    public static List<WrappedLine> WrapLinesDetailed(string? text, double maxWidth, Func<string, double> measure)
    {
        var result = new List<WrappedLine>();
        string normalised = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (string paragraph in normalised.Split('\n'))
        {
            var lines = new List<string>();
            if (maxWidth <= 0 || paragraph.Length == 0)
            {
                lines.Add(paragraph);
            }
            else
            {
                string[] words = paragraph.Split(' ');
                string current = words[0];
                for (int i = 1; i < words.Length; i++)
                {
                    string candidate = current + " " + words[i];
                    if (measure(candidate) <= maxWidth)
                    {
                        current = candidate;
                    }
                    else
                    {
                        AddFittingLines(lines, current, maxWidth, measure);
                        current = words[i];
                    }
                }
                AddFittingLines(lines, current, maxWidth, measure);
            }

            for (int i = 0; i < lines.Count; i++)
                result.Add(new WrappedLine(lines[i], EndsParagraph: i == lines.Count - 1));
        }
        return result;
    }

    /// <summary>Adds the text as one line if it fits, otherwise breaks it between characters until each piece does.</summary>
    private static void AddFittingLines(List<string> lines, string text, double maxWidth, Func<string, double> measure)
    {
        if (text.Length == 0 || measure(text) <= maxWidth)
        {
            lines.Add(text);
            return;
        }

        var piece = new StringBuilder();
        foreach (char c in text)
        {
            bool mayBreakBefore = piece.Length > 0 && !char.IsLowSurrogate(c) && !IsCombiningMark(c);
            if (mayBreakBefore && measure(piece.ToString() + c) > maxWidth)
            {
                lines.Add(piece.ToString());
                piece.Clear();
            }
            piece.Append(c);
        }
        if (piece.Length > 0) lines.Add(piece.ToString());
    }

    private static bool IsCombiningMark(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    // ------------------------------------------------------------------ rotation / local frame

    /// <summary>Rotates a vector clockwise (on a y-down page) by the angle, about the origin — the same convention as
    /// WPF's RotateTransform, so what is computed here lines up with what is drawn.</summary>
    public static GeoPoint Rotate(GeoPoint p, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new GeoPoint(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
    }

    /// <summary>A point in the text's local frame, as a page point.</summary>
    public static GeoPoint LocalToPage(GeoPoint anchor, double rotationDegrees, GeoPoint local)
    {
        var r = Rotate(local, rotationDegrees);
        return new GeoPoint(anchor.X + r.X, anchor.Y + r.Y);
    }

    /// <summary>A page point expressed in the text's local frame (the inverse of <see cref="LocalToPage"/>).</summary>
    public static GeoPoint PageToLocal(GeoPoint anchor, double rotationDegrees, GeoPoint page) =>
        Rotate(new GeoPoint(page.X - anchor.X, page.Y - anchor.Y), -rotationDegrees);

    /// <summary>The four page-space corners (clockwise from the top-left) of a rectangle given in the local frame.</summary>
    public static GeoPoint[] RectangleCorners(GeoPoint anchor, double rotationDegrees, double left, double top, double right, double bottom) => new[]
    {
        LocalToPage(anchor, rotationDegrees, new GeoPoint(left, top)),
        LocalToPage(anchor, rotationDegrees, new GeoPoint(right, top)),
        LocalToPage(anchor, rotationDegrees, new GeoPoint(right, bottom)),
        LocalToPage(anchor, rotationDegrees, new GeoPoint(left, bottom)),
    };

    /// <summary>Whether a page point is inside the text box, which is <paramref name="width"/> x <paramref name="height"/> in
    /// the local frame and grown by <paramref name="margin"/> on every side (the grab tolerance).</summary>
    public static bool BoxContains(GeoPoint anchor, double rotationDegrees, double width, double height, double margin, GeoPoint page)
    {
        var local = PageToLocal(anchor, rotationDegrees, page);
        return local.X >= -margin && local.X <= width + margin && local.Y >= -margin && local.Y <= height + margin;
    }

    // ------------------------------------------------------------------ blurred backdrop falloff

    /// <summary>0 at t = 0, 1 at t = 1, with zero slope at both ends — so a fade built from it has no visible "start" or "end".</summary>
    public static double SmoothStep(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>
    /// Width of the fade band of the blurred halo: its area (how far it reaches beyond the letters) times the softness (0-100%).
    /// 0 means a sharp edge; 100 fades across the whole area. All lengths in one unit (page units on screen, pixels when printing).
    /// </summary>
    public static double FeatherWidth(double area, double softnessPercent) =>
        Math.Max(0, area) * Math.Clamp(softnessPercent, 0, 100) / 100.0;

    /// <summary>
    /// How strongly the blurred copy of the page shows through (0 = not at all, 1 = fully) at a point that is
    /// <paramref name="distanceToText"/> away from the nearest part of a letter (0 on the letters themselves). The halo reaches
    /// <paramref name="area"/> from the letters. It is fully blurred out to <c>area - feather</c>, and from there fades to nothing at
    /// <c>area</c>, eased with <see cref="SmoothStep"/>. A feather of 0 is a sharp edge at exactly <paramref name="area"/>. The print
    /// path evaluates this per pixel, from the distance to the nearest ink; the overlay approximates the same profile with a stack of
    /// progressively narrower strokes round the letters (<see cref="HaloRings"/>), so the two agree.
    /// </summary>
    public static double HaloStrength(double distanceToText, double area, double feather)
    {
        if (distanceToText > area) return 0;
        if (feather <= 1e-6) return 1;

        double fadeStart = area - feather;
        if (distanceToText <= fadeStart) return 1;
        return 1 - SmoothStep((distanceToText - fadeStart) / feather);
    }

    /// <summary>
    /// The halo as a stack of strokes, for drawing it as geometry: each entry is a stroke of half-width <c>Radius</c> around the
    /// letters at opacity <c>Alpha</c>, widest first, to be drawn in that order. Stacked, their combined opacity at distance d is
    /// <see cref="HaloStrength"/> (to within one band). Each band's alpha is chosen so that the opacities COMPOSE to the target: with
    /// T(k) the wanted strength in band k, (1 - alpha(k)) = (1 - T(k)) / (1 - T(k-1)). The last entry is a solid core out to where
    /// the fade starts. With no feather there is a single sharp ring. <paramref name="bandCount"/> is how many bands the fade is
    /// cut into — enough that each is about a pixel or two wide, and the steps are invisible.
    /// </summary>
    public static List<(double Radius, double Alpha)> HaloRings(double area, double feather, int bandCount)
    {
        var rings = new List<(double Radius, double Alpha)>();
        if (area <= 0) return rings;
        if (feather <= 1e-6 || bandCount < 1)
        {
            rings.Add((area, 1));
            return rings;
        }

        feather = Math.Min(feather, area);
        double fadeStart = area - feather;
        double previousTarget = 0;
        for (int k = 0; k < bandCount; k++)
        {
            double outer = area - feather * k / bandCount;
            double inner = area - feather * (k + 1) / bandCount;
            double target = HaloStrength((outer + inner) / 2, area, feather);
            double alpha = 1 - (1 - target) / Math.Max(1e-9, 1 - previousTarget);
            rings.Add((outer, Math.Clamp(alpha, 0, 1)));
            previousTarget = target;
        }
        // The solid core. When the fade runs the whole way in (softness 100%) it has no width of its own, but it still has to exist:
        // drawn as a stroke plus a fill, it is the fill that makes the letters' interiors exactly full strength.
        rings.Add((Math.Max(fadeStart, 1e-3), 1));
        return rings;
    }

    // ------------------------------------------------------------------ mirroring

    /// <summary>
    /// Where a text box goes when the page is mirrored about a vertical line (<paramref name="horizontal"/>, the line x = axis) or a
    /// horizontal one (y = axis), with the LETTERS left readable. The box occupies exactly the mirror image of the region it covered, but its
    /// text still runs left to right and top to bottom: so the rotation is reflected (a box tilted +20 degrees comes out tilted -20) and the
    /// anchor — the top-left corner the text hangs from — moves to wherever the mirrored box's top-left corner now is, which is the mirror
    /// image of a DIFFERENT corner of the original (its top-right after a horizontal mirror, its bottom-left after a vertical one).
    /// </summary>
    public static (GeoPoint Anchor, double RotationDegrees) MirrorTextBox(
        GeoPoint anchor, double rotationDegrees, double width, double height, bool horizontal, double axis)
    {
        double radians = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        var mirroredAnchor = horizontal
            ? new GeoPoint(2 * axis - anchor.X - width * cos, anchor.Y + width * sin)
            : new GeoPoint(anchor.X - height * sin, 2 * axis - anchor.Y - height * cos);
        return (mirroredAnchor, -rotationDegrees);
    }

    /// <summary>Axis-aligned bounds of a set of points.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(IEnumerable<GeoPoint> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return (minX, minY, maxX, maxY);
    }
}
