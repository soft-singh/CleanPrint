using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>Everything that decides how a text annotation is laid out. All lengths are in page units (device-independent
/// units, 1/96 inch) — callers convert from the points the annotation stores (see <see cref="TextGeometry"/>). Colours are
/// not part of the layout: they are passed when drawing.</summary>
public sealed record TextRenderStyle(
    string Text,
    string FontFamily,
    double FontSizeDiu,
    bool Bold,
    bool Italic,
    bool Underline,
    double CharacterSpacingDiu,
    double LineSpacing,
    double BoxWidthDiu,
    TextAlign Alignment = TextAlign.Left);

/// <summary>A laid-out text box: its size and the lines in it, all in page units.</summary>
public sealed class TextLayoutInfo
{
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double LineHeight { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>For each line: whether it is the last of its paragraph. Justified text leaves those ragged.</summary>
    public required IReadOnlyList<bool> EndsParagraph { get; init; }
}

/// <summary>A laid-out text box plus a drawing of it. The drawing's origin is the box's top-left, and it always includes
/// a transparent rectangle covering the whole box, so that anything showing it (an image, a brush) lines up with the
/// box instead of with the tight bounds of the glyphs.</summary>
public sealed class TextRenderResult
{
    public required TextLayoutInfo Layout { get; init; }
    public required DrawingGroup Drawing { get; init; }
}

/// <summary>
/// Lays out and draws text annotations — one implementation shared by the on-screen overlay (the letters as an opacity mask,
/// plus their outline as geometry), hit-testing (size only) and the printed page, so that what is seen is what prints.
///
/// Text is shaped by WPF's own FormattedText, so complex scripts (Gurmukhi, Devanagari, Arabic...) come out correctly. The
/// one thing FormattedText cannot do is letter-spacing, so when a non-zero character spacing is asked for, each character
/// is placed individually instead — fine for Latin and similar scripts, but it cannot apply a script's joining/reordering
/// rules, which is why the spacing-free path is the default.
///
/// Where everything goes — which string at which point, and where the underlines run — is decided once, in
/// <see cref="Place"/>; drawing the fill, drawing the outline and building the outline geometry all start from that.
/// </summary>
public static class TextRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentUICulture;

    /// <summary>Wrapping, size and line height only — no drawing (what hit-testing needs).</summary>
    public static TextLayoutInfo Measure(TextRenderStyle style) => Layout(style, out _, out _);

    /// <summary>
    /// Lays the text out and draws it. <paramref name="fill"/> colours the letters (null = no fill, outline only);
    /// <paramref name="stroke"/> outlines them (null = no outline). Both null draws nothing but still reports the layout.
    /// </summary>
    public static TextRenderResult Render(TextRenderStyle style, Brush? fill, Pen? stroke)
    {
        var layout = Layout(style, out var typeface, out double em);
        var placed = Place(layout, style, typeface, em);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Pins the drawing's bounds to the whole box (a transparent fill still counts towards bounds).
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, layout.Width, layout.Height));

            // Plain filled text goes through DrawText: crisp, hinted, and FormattedText can underline it itself. Anything that
            // needs an OUTLINE has to be drawn as geometry instead, and geometry carries no underline of its own.
            bool asGeometry = stroke is not null;
            foreach (var piece in placed.Pieces)
            {
                if (asGeometry)
                {
                    // BuildGeometry has nothing to return for a piece with no visible glyphs (a lone space, with character spacing on).
                    var shape = MakeText(piece.Text, typeface, em, Brushes.Black).BuildGeometry(piece.Origin);
                    if (shape is not null) context.DrawGeometry(fill, stroke, shape);
                }
                else if (fill is not null)
                {
                    var text = MakeText(piece.Text, typeface, em, fill);
                    if (style.Underline && style.CharacterSpacingDiu == 0) text.SetTextDecorations(TextDecorations.Underline);
                    context.DrawText(text, piece.Origin);
                }
            }

            bool drawUnderlines = style.Underline && (asGeometry || style.CharacterSpacingDiu != 0);
            if (drawUnderlines)
            {
                var underlineBrush = fill ?? stroke?.Brush;
                if (underlineBrush is not null)
                    foreach (var line in placed.Underlines)
                        context.DrawRectangle(underlineBrush, null, new Rect(line.X1, line.Y - placed.UnderlineThickness / 2, line.X2 - line.X1, placed.UnderlineThickness));
            }
        }

        return new TextRenderResult { Layout = layout, Drawing = visual.Drawing };
    }

    /// <summary>The letter shapes (and underlines) as geometry in page units, with the box's top-left as origin — what the
    /// overlay strokes to draw the text's outline.</summary>
    public static Geometry BuildOutline(TextRenderStyle style)
    {
        var layout = Layout(style, out var typeface, out double em);
        var placed = Place(layout, style, typeface, em);

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var piece in placed.Pieces)
        {
            // BuildGeometry has nothing to return for a piece with no visible glyphs (a lone space, with character spacing on);
            // adding that nothing to the group would throw.
            var shape = MakeText(piece.Text, typeface, em, Brushes.Black).BuildGeometry(piece.Origin);
            if (shape is not null) group.Children.Add(shape);
        }
        if (style.Underline)
            foreach (var line in placed.Underlines)
                group.Children.Add(new RectangleGeometry(new Rect(line.X1, line.Y - placed.UnderlineThickness / 2, line.X2 - line.X1, placed.UnderlineThickness)));
        return group;
    }

    /// <summary>
    /// Renders text for the printed page: scaled to page pixels, rotated clockwise about the box's top-left, at the given opacity,
    /// into the smallest bitmap that holds the result. <c>OffsetX/OffsetY</c> say where the bitmap's top-left sits relative to the
    /// annotation's anchor (negative when rotation swings the text up or left of it). Premultiplied BGRA, ready to composite.
    /// <paramref name="strokeThicknessDiu"/> is the outline's width in page units. Needs the UI thread (it renders through WPF).
    /// Null if there is nothing to draw or the bitmap would be absurdly large.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int OffsetX, int OffsetY)? RasterizeForPrint(
        TextRenderStyle style, Color? fillColor, Color? strokeColor, double strokeThicknessDiu, double opacity,
        double rotationDegrees, double pixelsPerDiu)
    {
        if (string.IsNullOrEmpty(style.Text) || style.FontSizeDiu * pixelsPerDiu < 1) return null;
        if (fillColor is null && strokeColor is null) return null;

        Brush? fill = fillColor is { } f ? new SolidColorBrush(Color.FromRgb(f.R, f.G, f.B)) : null;
        Pen? stroke = strokeColor is { } s
            ? new Pen(new SolidColorBrush(Color.FromRgb(s.R, s.G, s.B)), Math.Max(0.1, strokeThicknessDiu)) { LineJoin = PenLineJoin.Round }
            : null;
        var result = Render(style, fill, stroke);
        double boxWidth = result.Layout.Width * pixelsPerDiu, boxHeight = result.Layout.Height * pixelsPerDiu;

        // The outline sticks out past the box by half its width.
        double bleed = stroke is null ? 0 : stroke.Thickness * pixelsPerDiu / 2 + 1;
        var bounds = TextGeometry.BoundsOf(TextGeometry.RectangleCorners(new GeoPoint(0, 0), rotationDegrees, -bleed, -bleed, boxWidth + bleed, boxHeight + bleed));
        int offsetX = (int)Math.Floor(bounds.MinX) - 1, offsetY = (int)Math.Floor(bounds.MinY) - 1;
        int width = (int)Math.Ceiling(bounds.MaxX) - offsetX + 1, height = (int)Math.Ceiling(bounds.MaxY) - offsetY + 1;
        if (width <= 2 || height <= 2 || (long)width * height > 60_000_000) return null;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Pushed outermost-first, applied to the drawing innermost-first: scale to pixels, rotate about the
            // anchor (the origin), then shift into the bitmap. The opacity covers fill and outline together.
            context.PushOpacity(Math.Clamp(opacity, 0, 1));
            context.PushTransform(new TranslateTransform(-offsetX, -offsetY));
            context.PushTransform(new RotateTransform(rotationDegrees));
            context.PushTransform(new ScaleTransform(pixelsPerDiu, pixelsPerDiu));
            context.DrawDrawing(result.Drawing);
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height, offsetX, offsetY);
    }

    /// <summary>
    /// Renders a text's FRAME for the printed page, the same way <see cref="RasterizeForPrint"/> renders the letters: a rectangle round the text box (<paramref name="paddingDiu"/> out from it on every side, with
    /// rounded corners if asked), its line in the given colour, width and dash style and its interior in the fill colour, at one opacity for the whole frame. Scaled to page pixels, rotated clockwise about the
    /// box's top-left and placed in the smallest bitmap that holds it; <c>OffsetX/OffsetY</c> are relative to the annotation's anchor. Premultiplied BGRA. Needs the UI thread. Null if there is nothing to draw
    /// (no line and no fill) or the bitmap would be absurdly large.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int OffsetX, int OffsetY)? RasterizeFrameForPrint(
        TextRenderStyle style, Color? lineColor, double lineWidthDiu, FrameLineStyle lineStyle, Color? fillColor, double opacity,
        double paddingDiu, double cornerRadiusDiu, double rotationDegrees, double pixelsPerDiu)
    {
        bool hasLine = lineColor is not null && lineWidthDiu > 0;
        if (!hasLine && fillColor is null) return null;

        var layout = Measure(style);
        Brush? fill = fillColor is { } f ? new SolidColorBrush(Color.FromRgb(f.R, f.G, f.B)) : null;
        Pen? pen = null;
        if (hasLine)
        {
            var c = lineColor!.Value;
            // Dash lengths are in multiples of the line's width — the same meaning as the screen's StrokeDashArray.
            pen = new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), lineWidthDiu) { LineJoin = PenLineJoin.Round };
            var dashes = FrameLineStyles.DashArray(lineStyle);
            if (dashes.Length > 0) pen.DashStyle = new DashStyle(dashes, 0);
        }

        double padding = Math.Max(0, paddingDiu);
        double bleed = (padding + (pen?.Thickness ?? 0) / 2) * pixelsPerDiu + 1;
        double boxWidth = layout.Width * pixelsPerDiu, boxHeight = layout.Height * pixelsPerDiu;
        var bounds = TextGeometry.BoundsOf(TextGeometry.RectangleCorners(new GeoPoint(0, 0), rotationDegrees, -bleed, -bleed, boxWidth + bleed, boxHeight + bleed));
        int offsetX = (int)Math.Floor(bounds.MinX) - 1, offsetY = (int)Math.Floor(bounds.MinY) - 1;
        int width = (int)Math.Ceiling(bounds.MaxX) - offsetX + 1, height = (int)Math.Ceiling(bounds.MaxY) - offsetY + 1;
        if (width <= 2 || height <= 2 || (long)width * height > 60_000_000) return null;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushOpacity(Math.Clamp(opacity, 0, 1));
            context.PushTransform(new TranslateTransform(-offsetX, -offsetY));
            context.PushTransform(new RotateTransform(rotationDegrees));
            context.PushTransform(new ScaleTransform(pixelsPerDiu, pixelsPerDiu));
            double radius = Math.Max(0, cornerRadiusDiu);
            context.DrawRoundedRectangle(fill, pen, new Rect(-padding, -padding, layout.Width + 2 * padding, layout.Height + 2 * padding), radius, radius);
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height, offsetX, offsetY);
    }

    // ------------------------------------------------------------------ layout

    /// <summary>One string to be drawn at one point (a whole line, or a single character when spacing is applied).</summary>
    private readonly record struct Piece(string Text, Point Origin);

    /// <summary>Where an underline runs: from X1 to X2 at height Y (the line's own top is already included).</summary>
    private readonly record struct UnderlineRun(double X1, double X2, double Y);

    private sealed record Placed(List<Piece> Pieces, List<UnderlineRun> Underlines, double UnderlineThickness);

    /// <summary>Decides, once, where every piece of text and every underline goes — including each line's alignment within the box.
    /// Everything that draws text starts here.</summary>
    private static Placed Place(TextLayoutInfo layout, TextRenderStyle style, Typeface typeface, double em)
    {
        double spacing = style.CharacterSpacingDiu;
        double baseline = MakeText("Ag", typeface, em, Brushes.Black).Baseline;
        var measure = MakeMeasure(typeface, em, spacing);
        var pieces = new List<Piece>();
        var underlines = new List<UnderlineRun>();

        // Puts one run of text at (x, top) and returns where it ends. With character spacing every character is placed on its own.
        double PlaceRun(string run, double x, double top)
        {
            if (spacing == 0)
            {
                pieces.Add(new Piece(run, new Point(x, top)));
                return x + measure(run);
            }
            foreach (char c in run)
            {
                pieces.Add(new Piece(c.ToString(), new Point(x, top)));
                x += MakeText(c.ToString(), typeface, em, Brushes.Black).WidthIncludingTrailingWhitespace + spacing;
            }
            return Math.Max(0, x - spacing);
        }

        for (int i = 0; i < layout.Lines.Count; i++)
        {
            // Trailing spaces are not part of how a line looks, so they must not push a centred or right-aligned line off-centre.
            string line = layout.Lines[i].TrimEnd();
            double top = i * layout.LineHeight;
            if (line.Length == 0) continue;

            double width = measure(line);
            if (style.Alignment == TextAlign.Justify && !layout.EndsParagraph[i] && layout.Width > width)
            {
                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2)
                {
                    // Spread the words over the full width: the leftover space is shared equally between the gaps.
                    double gap = (layout.Width - words.Sum(measure)) / (words.Length - 1);
                    double x = 0;
                    foreach (var word in words)
                        x = PlaceRun(word, x, top) + gap;
                    underlines.Add(new UnderlineRun(0, layout.Width, top + baseline + em * 0.12));
                    continue;
                }
            }

            double start = style.Alignment switch
            {
                TextAlign.Center => Math.Max(0, (layout.Width - width) / 2),
                TextAlign.Right => Math.Max(0, layout.Width - width),
                _ => 0,
            };
            double end = PlaceRun(line, start, top);
            underlines.Add(new UnderlineRun(start, end, top + baseline + em * 0.12));
        }

        return new Placed(pieces, underlines, Math.Max(0.5, em * 0.06));
    }

    /// <summary>"How wide is this string as it will be drawn" — with character spacing, the sum of the characters plus the spacing.</summary>
    private static Func<string, double> MakeMeasure(Typeface typeface, double em, double spacing)
    {
        if (spacing == 0)
            return line => line.Length == 0 ? 0 : MakeText(line, typeface, em, Brushes.Black).WidthIncludingTrailingWhitespace;

        var widths = new Dictionary<char, double>();
        return line =>
        {
            double total = 0;
            foreach (char c in line)
            {
                if (!widths.TryGetValue(c, out double w))
                    widths[c] = w = MakeText(c.ToString(), typeface, em, Brushes.Black).WidthIncludingTrailingWhitespace;
                total += w + spacing;
            }
            return Math.Max(0, total - (line.Length > 0 ? spacing : 0));
        };
    }

    private static TextLayoutInfo Layout(TextRenderStyle style, out Typeface typeface, out double em)
    {
        typeface = new Typeface(new FontFamily(style.FontFamily),
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        em = Math.Max(1, style.FontSizeDiu);

        var measure = MakeMeasure(typeface, em, style.CharacterSpacingDiu);
        var wrapped = TextGeometry.WrapLinesDetailed(style.Text, style.BoxWidthDiu, measure);
        var lines = wrapped.Select(l => l.Text).ToList();
        double natural = MakeText("Ag", typeface, em, Brushes.Black).Height;
        double lineHeight = natural * Math.Max(0.5, style.LineSpacing);
        double widest = lines.Count == 0 ? 0 : lines.Max(measure);

        // The box is as tall as its lines need — and the LAST line needs its own full height, not "one line height": line spacing
        // moves the lines apart, but it says nothing about how tall a line of text is. Using lines x lineHeight cut the bottom off
        // the last line when the spacing was tightened (descenders like y and g went first) and added ever more empty space below
        // it when the spacing was widened. A glyph can also hang below its line box; that overhang is added so it is never clipped.
        string lastText = lines.LastOrDefault(l => l.Trim().Length > 0) ?? "Ag";
        double overhang = Math.Max(0, MakeText(lastText.Trim(), typeface, em, Brushes.Black).OverhangAfter);
        double height = Math.Max(0, lines.Count - 1) * lineHeight + natural + overhang;

        return new TextLayoutInfo
        {
            // A fixed-width box is exactly that wide; an automatic one is as wide as its widest line (with a floor, so an
            // empty box that has just been placed is still a visible, clickable rectangle).
            Width = style.BoxWidthDiu > 0 ? style.BoxWidthDiu : Math.Max(widest, em * 3),
            Height = height,
            LineHeight = lineHeight,
            Lines = lines,
            EndsParagraph = wrapped.Select(l => l.EndsParagraph).ToList(),
        };
    }

    private static FormattedText MakeText(string text, Typeface typeface, double em, Brush brush) =>
        new(text, Culture, FlowDirection.LeftToRight, typeface, em, brush, 1.0);
}
