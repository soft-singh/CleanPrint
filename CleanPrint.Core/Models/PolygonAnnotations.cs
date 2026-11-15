using System;
using System.Collections.Generic;

namespace CleanPrint.Core.Models;

/// <summary>Which rendered version of a page a polygon applies to — kept separate since
/// cropping/whitening can change pixel dimensions and alignment between the two.</summary>
public enum ImageVariant
{
    Raw,
    Cleaned,
}

/// <summary>
/// What a polygon is used for. FreezeRow/FreezeColumn (pinned-while-scrolling overlay) and
/// HideRow/HideColumn (actual gap-closing removal) are both fully implemented.
/// </summary>
public enum PolygonPurpose
{
    None,
    FreezeRow,
    FreezeColumn,
    HideRow,
    HideColumn,
}

/// <summary>
/// What kind of shape an annotation is, i.e. how its <see cref="PolygonAnnotation.Vertices"/> are to be read.
/// Stored as a number in the JSON, with Polygon = 0, so every annotation saved before kinds existed (which
/// have no Kind at all) loads as the plain polygon it always was.
///
/// Anything other than Polygon keeps only its few CONTROL points as vertices (that's what you drag), and the
/// drawn/printed outline is computed from them by <see cref="ShapeGeometry.Flatten"/> — a true curve sampled
/// finely enough that no straight segment is visible, rather than a ring of many editable vertices.
/// </summary>
public enum ShapeKind
{
    /// <summary>An ordinary polygon/polyline/rectangle: the vertices ARE the outline.</summary>
    Polygon = 0,

    /// <summary>Axis-aligned ellipse. Vertices: [centre, right-hand extent (cx+rx, cy), bottom extent (cx, cy+ry)].</summary>
    Ellipse = 1,

    /// <summary>Circle from its centre and a point on its edge. Vertices: [centre, edge point].</summary>
    CircleCenter = 2,

    /// <summary>Circle through three points. Vertices: [p1, p2, p3].</summary>
    Circle3 = 3,

    /// <summary>Circular arc from the first point to the third, passing through the second. Vertices: [start, through, end].</summary>
    Arc3 = 4,

    /// <summary>Smooth curve (Catmull-Rom spline) through its vertices; open, or a closed loop when IsClosed.</summary>
    Curve = 5,

    /// <summary>A text label. Vertices: [top-left anchor]. Uses <see cref="PolygonAnnotation.Text"/> and FontSize.</summary>
    Text = 6,

    /// <summary>
    /// A picture placed on the page. Vertices: [top-left anchor]; the box is <see cref="PolygonAnnotation.ImageWidth"/> x
    /// <see cref="PolygonAnnotation.ImageHeight"/> page units, rotated clockwise about the anchor by RotationDegrees (exactly as a text box is), drawn at
    /// FillOpacity percent, mirrored within its box if FlipHorizontal / FlipVertical. The picture itself is the file
    /// <see cref="PolygonAnnotation.ImageFile"/> in the job's own images folder, so it travels with the job.
    /// </summary>
    Image = 7,
}

/// <summary>How the frame round a text annotation is drawn: a continuous line, or one of several dashed ones.</summary>
public enum FrameLineStyle
{
    Solid,
    Dash,
    Dot,
    DashDot,
    LongDash,
}

/// <summary>What each <see cref="FrameLineStyle"/> means as a dash pattern. The numbers are lengths in multiples of the line's thickness — the convention of WPF's StrokeDashArray
/// and DashStyle, which is what draws the frame both on screen and in print, so one pattern serves both.</summary>
public static class FrameLineStyles
{
    /// <summary>The dash pattern (alternating dash and gap lengths, in line widths), or an empty array for a solid line.</summary>
    public static double[] DashArray(FrameLineStyle style) => style switch
    {
        FrameLineStyle.Dash => new[] { 4.0, 3.0 },
        FrameLineStyle.Dot => new[] { 1.0, 2.0 },
        FrameLineStyle.DashDot => new[] { 4.0, 2.0, 1.0, 2.0 },
        FrameLineStyle.LongDash => new[] { 8.0, 3.0 },
        _ => System.Array.Empty<double>(),
    };

    public static string Label(FrameLineStyle style) => style switch
    {
        FrameLineStyle.Dash => "Dashed",
        FrameLineStyle.Dot => "Dotted",
        FrameLineStyle.DashDot => "Dash-dot",
        FrameLineStyle.LongDash => "Long dash",
        _ => "Solid",
    };
}

/// <summary>Horizontal alignment of text within its box.</summary>
public enum TextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,

    /// <summary>Lines of a wrapped paragraph are spread to the full width of the box by widening the gaps between words; the last
    /// line of each paragraph stays left-aligned, as in a word processor.</summary>
    Justify = 3,
}

/// <summary>A single point of a polygon's outline, in WPF device-independent units matching the
/// rendered bitmap's logical size (the same coordinate space PreviewImage.Width/Height and any
/// WPF Geometry drawn over it use) — NOT raw source-pixel count.</summary>
public sealed class PolygonPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// One user-drawn polygon: its outline, whether it's closed into a loop, what it's used for, and
/// its own display style (line color/thickness, and — only rendered while closed — an optional
/// interior fill color/opacity) — freely changeable at any time, like a CAD entity's properties,
/// independent of every other polygon on the page.
/// </summary>
public sealed class PolygonAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>How Vertices are interpreted — see <see cref="ShapeKind"/>. Defaults to Polygon, which is
    /// also what annotations saved before this property existed deserialize as.</summary>
    public ShapeKind Kind { get; set; } = ShapeKind.Polygon;

    /// <summary>The text of a ShapeKind.Text annotation (null/empty for every other kind).</summary>
    public string? Text { get; set; }

    /// <summary>Font size of a ShapeKind.Text annotation, in the same page units as the vertices
    /// (device-independent units of the rendered page, NOT raw pixels). Ignored for other kinds.</summary>
    public double FontSize { get; set; } = 24.0;

    // ---- Text formatting (ShapeKind.Text only; ignored for every other kind). All sizes are in POINTS, like a word
    // processor, so "12" here is the same 12 as in Word: at 100% zoom a 12pt label is 16 device-independent units tall.

    /// <summary>Font size in points. Null in annotations saved before text sizes were points: those stored
    /// <see cref="FontSize"/> in page units, and 1 point = 96/72 of a unit, so they read back as FontSize x 0.75.</summary>
    public double? FontSizePoints { get; set; }

    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    /// <summary>Clockwise rotation in degrees about the text's top-left (its anchor point).</summary>
    public double RotationDegrees { get; set; }

    /// <summary>Extra space added after every character, in points (negative condenses). 0 = the font's own spacing.</summary>
    public double CharacterSpacingPoints { get; set; }

    /// <summary>Line height as a multiple of the font's natural line height (1.0 = single spacing).</summary>
    public double LineSpacing { get; set; } = 1.0;

    /// <summary>Width of the text box in points; text wraps to it. 0 = automatic (no wrapping beyond explicit line breaks).</summary>
    public double BoxWidthPoints { get; set; }

    /// <summary>Blur the page behind the text — in a halo of <see cref="BlurAreaPoints"/> all round the LETTERS themselves, not
    /// round their bounding box — so the text reads clearly over busy graphics. On by default; applies to the output too when the
    /// text is included in print.</summary>
    public bool BlurBackground { get; set; } = true;

    /// <summary>How far, in points, the blurred halo extends beyond the letters.</summary>
    public double BlurAreaPoints { get; set; } = 8.0;

    /// <summary>Horizontal alignment of the lines within the text box (the box's width is the fixed width, or the widest line when automatic).</summary>
    public TextAlign Alignment { get; set; } = TextAlign.Left;

    /// <summary>How gradually the blur fades out towards the edge of its area, 0-100: 0 is a sharp edge, 100 fades
    /// across the whole area (full strength only right beside the letters). See <see cref="TextGeometry.HaloStrength"/>.</summary>
    public double BlurSoftnessPercent { get; set; } = 70.0;

    // ---- Frame (ShapeKind.Text only): a rectangle round the text, drawn behind the letters. Its own line, colour, fill and opacity — separate from the letters' fill, outline and opacity.
    // Annotations saved before this existed have none of these in their file and read back with the defaults below, i.e. no frame.

    /// <summary>Whether the frame is drawn at all.</summary>
    public bool FrameVisible { get; set; }

    /// <summary>The frame's line colour.</summary>
    public string FrameStrokeColorHex { get; set; } = "#000000";

    /// <summary>The frame's line width, in page units. 0 = no line (a filled box without an edge).</summary>
    public double FrameThickness { get; set; } = 1.5;

    /// <summary>Solid or one of the dashed styles.</summary>
    public FrameLineStyle FrameLineStyle { get; set; } = FrameLineStyle.Solid;

    /// <summary>The frame's interior colour. Null/empty = not filled (the page shows through).</summary>
    public string? FrameFillColorHex { get; set; }

    /// <summary>Opacity of the whole frame (line and fill together), 0-100. Independent of the text's own opacity.</summary>
    public double FrameOpacity { get; set; } = 100.0;

    /// <summary>Space between the text box and the frame's line, in points, on every side.</summary>
    public double FramePaddingPoints { get; set; } = 4.0;

    /// <summary>Corner radius of the frame, in points (0 = square corners).</summary>
    public double FrameCornerRadiusPoints { get; set; }

    /// <summary>
    /// How this text annotation's colours are to be read. 0 (annotations saved before text had its own fill/stroke): the
    /// text colour was stored in <see cref="StrokeColorHex"/>. 1 (current): like any other shape — FillColorHex is the colour of
    /// the letters (none = outline only), StrokeColorHex/StrokeThickness/HasStroke an outline around them, and FillOpacity (0-100)
    /// the opacity of the whole text. Version 0 annotations are converted on load (see MainViewModel.PolygonFromAnnotation).
    /// </summary>
    public int TextStyleVersion { get; set; }

    /// <summary>Font family name of a ShapeKind.Text annotation. Annotations saved before this existed have none
    /// and get Segoe UI, the face text always used. Ignored for other kinds.</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    public List<PolygonPoint> Vertices { get; set; } = new();
    public bool IsClosed { get; set; }
    public PolygonPurpose Purpose { get; set; } = PolygonPurpose.None;

    /// <summary>Hex color string, e.g. "#FF1E90FF" (ARGB) or "#1E90FF" (RGB).</summary>
    public string StrokeColorHex { get; set; } = "#1E90FF";
    public double StrokeThickness { get; set; } = 3.0;

    /// <summary>True by default. False means the outline isn't drawn at all — only offered/
    /// meaningful when FillColorHex is also set, since removing the outline of an unfilled polygon
    /// would make it invisible.</summary>
    public bool HasStroke { get; set; } = true;

    /// <summary>Interior fill color, only rendered while the polygon is closed. Null/empty means
    /// no fill at all (the default) — this is opt-in per polygon.</summary>
    public string? FillColorHex { get; set; }

    /// <summary>Fill opacity as a percentage (0-100). Only meaningful when FillColorHex is set.</summary>
    public double FillOpacity { get; set; } = 50.0;

    /// <summary>False by default (opt-in). When true, this polygon's outline/fill is drawn directly
    /// onto the actual printed/exported page — everything else about a polygon is purely a preview/
    /// editing aid that never appears in the output.
    ///
    /// For a Hide Row / Hide Column polygon the same flag means something different (there is no outline
    /// worth printing): "apply this hide to the printed/exported page". Off by default, so a hide is
    /// a preview/editing aid until it is explicitly included.</summary>
    public bool IncludeInPrint { get; set; }

    // ---- Image (ShapeKind.Image) ----

    /// <summary>File name (not path) of the picture, inside the job folder's "images" folder. Null for every other kind.</summary>
    public string? ImageFile { get; set; }

    /// <summary>The picture's box in page units (before rotation). 0 for every other kind.</summary>
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }

    /// <summary>The picture is mirrored left-right / top-bottom WITHIN its box. Set by mirroring a selection that includes it — a text label is
    /// kept readable when mirrored, but a picture is meant to be flipped.</summary>
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
}

/// <summary>All polygons drawn on one page, one image variant.</summary>
public sealed class PageAnnotations
{
    public List<PolygonAnnotation> Polygons { get; set; } = new();
}

/// <summary>All polygon annotations for one print job, keyed by page number then image variant.</summary>
public sealed class JobAnnotationSettings
{
    public Dictionary<int, Dictionary<ImageVariant, PageAnnotations>> Pages { get; set; } = new();
}
