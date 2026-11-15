using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using CleanPrint.Core.Models;

namespace CleanPrint.App.ViewModels;

/// <summary>A polygon vertex, in WPF device-independent units matching the rendered bitmap's
/// logical size. Plain mutable data — vertex lists are replaced wholesale on every edit rather
/// than mutated in place, so this doesn't need its own change notification.</summary>
public sealed class PolygonVertexViewModel
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// One polygon being drawn/edited/displayed: its vertices (raw + zoom-scaled display versions),
/// whether it's closed, its purpose, and its own line color/thickness — all independently
/// changeable at any time, like a CAD entity's properties, regardless of every other polygon on
/// the page.
/// </summary>
public sealed class PolygonViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>How Vertices are interpreted — see ShapeKind. Anything but Polygon keeps only a few CONTROL
    /// points as its vertices (the draggable handles); the drawn outline is computed from them (see
    /// MainViewModel.RefreshPolygonDisplay and ShapeGeometry).</summary>
    private ShapeKind _kind = ShapeKind.Polygon;
    public ShapeKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(IsImage));
            RaiseTextVisibilityChanged();
            RaiseImageVisibilityChanged();
            OnPropertyChanged(nameof(HasFixedPointCount));
            OnPropertyChanged(nameof(HasFill));
            OnPropertyChanged(nameof(FillBrush));
            OnPropertyChanged(nameof(TextOpacity));
        }
    }

    /// <summary>True for the kinds whose control-point count is fixed (circles, ellipses, arcs, text) — so
    /// adding or deleting a point is meaningless for them. Free-count kinds are Polygon and Curve.</summary>
    public bool HasFixedPointCount => ShapeGeometry.HasFixedPointCount(Kind);

    public bool IsText => Kind == ShapeKind.Text;

    /// <summary>The text of a Text annotation (may contain line breaks).</summary>
    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value ?? ""; OnPropertyChanged(); RaiseTextVisibilityChanged(); }
    }

    // ---- Text formatting. Sizes are in POINTS, like a word processor (12 here is Word's 12), and are converted to page
    // units only where something is drawn or measured (TextGeometry.PointsToDiu). All of these affect how the text is
    // laid out, so MainViewModel re-lays it out after changing any of them (RefreshPolygonDisplay).

    private double _fontSizePoints = 12.0;
    public double FontSizePoints
    {
        get => _fontSizePoints;
        set { _fontSizePoints = Math.Max(1, value); OnPropertyChanged(); }
    }

    /// <summary>Font family of a Text annotation (a name from the installed fonts; Segoe UI by default).</summary>
    private string _fontFamilyName = "Segoe UI";
    public string FontFamilyName
    {
        get => _fontFamilyName;
        set { _fontFamilyName = string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value; OnPropertyChanged(); }
    }

    /// <summary>Horizontal alignment of the lines within the box (left, centre, right, justified).</summary>
    private TextAlign _alignment = TextAlign.Left;
    public TextAlign Alignment { get => _alignment; set { _alignment = value; OnPropertyChanged(); } }

    private bool _bold;
    public bool Bold { get => _bold; set { _bold = value; OnPropertyChanged(); } }

    private bool _italic;
    public bool Italic { get => _italic; set { _italic = value; OnPropertyChanged(); } }

    private bool _underline;
    public bool Underline { get => _underline; set { _underline = value; OnPropertyChanged(); } }

    /// <summary>Clockwise degrees about the top-left of the text box. Bound by the overlay's RotateTransform.</summary>
    private double _rotationDegrees;
    public double RotationDegrees { get => _rotationDegrees; set { _rotationDegrees = value; OnPropertyChanged(); } }

    /// <summary>Extra space after every character, in points.</summary>
    private double _characterSpacingPoints;
    public double CharacterSpacingPoints { get => _characterSpacingPoints; set { _characterSpacingPoints = value; OnPropertyChanged(); } }

    /// <summary>Line height as a multiple of the font's natural line height.</summary>
    private double _lineSpacing = 1.0;
    public double LineSpacing { get => _lineSpacing; set { _lineSpacing = Math.Max(0.5, value); OnPropertyChanged(); } }

    /// <summary>Width of the text box in points (text wraps to it); 0 = automatic.</summary>
    private double _boxWidthPoints;
    public double BoxWidthPoints { get => _boxWidthPoints; set { _boxWidthPoints = Math.Max(0, value); OnPropertyChanged(); } }

    /// <summary>Whether the page behind the text is blurred so the text reads clearly over graphics.</summary>
    private bool _blurBackground = true;
    public bool BlurBackground
    {
        get => _blurBackground;
        set { _blurBackground = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowBlur)); }
    }

    /// <summary>How far, in points, the blurred backdrop extends beyond the text box on every side.</summary>
    private double _blurAreaPoints = 8.0;
    public double BlurAreaPoints { get => _blurAreaPoints; set { _blurAreaPoints = Math.Max(0, value); OnPropertyChanged(); } }

    /// <summary>How gradually the blur fades out towards the edge of its area, 0-100 (0 = a hard edge).</summary>
    private double _blurSoftnessPercent = 70.0;
    public double BlurSoftnessPercent { get => _blurSoftnessPercent; set { _blurSoftnessPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); } }

    // ---- Frame: a rectangle round the text box, drawn behind the letters, with its own line style, colour, thickness, fill and opacity (see PolygonAnnotation.Frame*). The sizes here are in page
    // units / points; MainViewModel.UpdateTextDisplay turns them into the on-screen Frame*Display values for the current zoom.

    private bool _frameVisible;
    public bool FrameVisible { get => _frameVisible; set { _frameVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFrame)); } }

    private Color _frameStrokeColor = Colors.Black;
    public Color FrameStrokeColor { get => _frameStrokeColor; set { _frameStrokeColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(FrameStrokeBrush)); } }

    /// <summary>Line width in page units; 0 = no line.</summary>
    private double _frameThickness = 1.5;
    public double FrameThickness { get => _frameThickness; set { _frameThickness = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(FrameStrokeBrush)); } }

    private FrameLineStyle _frameLineStyle = FrameLineStyle.Solid;
    public FrameLineStyle FrameLineStyle { get => _frameLineStyle; set { _frameLineStyle = value; OnPropertyChanged(); OnPropertyChanged(nameof(FrameDashArray)); } }

    /// <summary>Interior colour of the frame; null = not filled.</summary>
    private Color? _frameFillColor;
    public Color? FrameFillColor { get => _frameFillColor; set { _frameFillColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(FrameFillBrush)); } }

    /// <summary>Opacity of the whole frame (line and fill), 0-100.</summary>
    private double _frameOpacity = 100.0;
    public double FrameOpacity { get => _frameOpacity; set { _frameOpacity = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(FrameOpacityFraction)); } }

    /// <summary>Gap between the text box and the frame's line, in points.</summary>
    private double _framePaddingPoints = 4.0;
    public double FramePaddingPoints { get => _framePaddingPoints; set { _framePaddingPoints = Math.Max(0, value); OnPropertyChanged(); } }

    /// <summary>Corner radius in points; 0 = square.</summary>
    private double _frameCornerRadiusPoints;
    public double FrameCornerRadiusPoints { get => _frameCornerRadiusPoints; set { _frameCornerRadiusPoints = Math.Max(0, value); OnPropertyChanged(); } }

    /// <summary>The frame shows when it is switched on, for a text annotation that has a position (also while empty text is being typed into, so it can be seen being sized).</summary>
    public bool ShowFrame => IsText && _frameVisible && _vertices.Count > 0;

    public Brush? FrameStrokeBrush => _frameThickness > 0 ? new SolidColorBrush(_frameStrokeColor) : null;
    public Brush? FrameFillBrush => _frameFillColor is { } c ? new SolidColorBrush(c) : null;
    public double FrameOpacityFraction => _frameOpacity / 100.0;

    /// <summary>The dash pattern, in multiples of the line width (what StrokeDashArray expects). Empty for a solid line.</summary>
    public DoubleCollection FrameDashArray => new(FrameLineStyles.DashArray(_frameLineStyle));

    // On-screen values at the current zoom, set by MainViewModel.UpdateTextDisplay. Margin/size of the frame, and how far the selection rectangle and the width handle are pushed out to clear it.
    private double _frameDisplayThickness = 1.5;
    public double FrameDisplayThickness { get => _frameDisplayThickness; set { _frameDisplayThickness = value; OnPropertyChanged(); } }

    private double _frameDisplayRadius;
    public double FrameDisplayRadius { get => _frameDisplayRadius; set { _frameDisplayRadius = value; OnPropertyChanged(); } }

    private double _frameDisplayPad;
    /// <summary>How far, on screen, the frame's line sits outside the text box.</summary>
    public double FrameDisplayPad
    {
        get => _frameDisplayPad;
        set
        {
            _frameDisplayPad = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameMargin));
            OnPropertyChanged(nameof(TextSelectionMargin));
            OnPropertyChanged(nameof(TextHandleMargin));
        }
    }

    /// <summary>Negative, so the frame's rectangle reaches out beyond the text box by the padding.</summary>
    public System.Windows.Thickness FrameMargin => new(-_frameDisplayPad);

    /// <summary>The dashed selection rectangle sits 4 outside the text — or outside the frame, if there is one (padding and half the line's width).</summary>
    public System.Windows.Thickness TextSelectionMargin => new(-(4 + FrameClearance));

    /// <summary>The width handle sits on the right edge of that rectangle.</summary>
    public System.Windows.Thickness TextHandleMargin => new(0, 0, -(8 + FrameClearance), 0);

    private double FrameClearance => ShowFrame ? _frameDisplayPad + _frameDisplayThickness / 2 : 0;

    /// <summary>True while the in-place text editor (MainWindow) is open over this annotation. The overlay's copy of the
    /// text hides meanwhile (its selection rectangle and blurred backdrop stay), so the editor isn't drawn over a second copy.</summary>
    private bool _isInlineEditing;
    public bool IsInlineEditing
    {
        get => _isInlineEditing;
        set { _isInlineEditing = value; OnPropertyChanged(); RaiseTextVisibilityChanged(); }
    }

    // ---- How text is coloured: exactly like any other shape. FillColor is the colour of the LETTERS (none = outline only),
    // StrokeColor/StrokeThickness/HasStroke an OUTLINE drawn around them, and FillOpacity (0-100) the opacity of the whole
    // text — so the toolbar's Color / Thickness / Fill / Opacity controls work on text the same way they do on a rectangle.

    /// <summary>The letters' colour, fully opaque (the opacity is applied to the whole text, outline included — see
    /// <see cref="TextOpacity"/>); null when the text has no fill and is drawn as an outline only.</summary>
    public Brush? TextFillBrush => FillColor is { } c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : null;

    /// <summary>Opacity of the whole text, 0-1, from the toolbar's Opacity box.</summary>
    public double TextOpacity => IsText ? FillOpacity / 100.0 : 1.0;

    /// <summary>The colour the in-place editor types in: the letters' fill, or the outline colour for outline-only text.</summary>
    public Brush EditorForeground => new SolidColorBrush(FillColor ?? StrokeColor);

    /// <summary>The text's letter shapes as geometry (zoom-scaled), to stroke as the outline — set by
    /// MainViewModel.UpdateTextDisplay, and only built while the text has an outline.</summary>
    private Geometry? _textOutline;
    public Geometry? TextOutline { get => _textOutline; set { _textOutline = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowTextOutline)); } }

    /// <summary>Outline thickness on screen (StrokeThickness x zoom), set with <see cref="TextOutline"/>.</summary>
    private double _textStrokeDisplayThickness = 1;
    public double TextStrokeDisplayThickness { get => _textStrokeDisplayThickness; set { _textStrokeDisplayThickness = value; OnPropertyChanged(); } }

    public bool ShowTextOutline => ShowText && HasStroke && _textOutline is not null;

    // ---- Computed by MainViewModel.RefreshPolygonDisplay for the overlay (zoom-scaled; DIU = page units) ----

    /// <summary>The laid-out text, drawn in opaque black, used as an OPACITY MASK over a TextBrush-coloured fill — so the
    /// text colour is just the fill's colour and changes instantly, with no re-layout.</summary>
    private Brush? _textMask;
    public Brush? TextMask { get => _textMask; set { _textMask = value; OnPropertyChanged(); } }

    /// <summary>Size of the text box on screen (page-unit size x zoom).</summary>
    private double _textDisplayWidth = 60, _textDisplayHeight = 20;
    public double TextDisplayWidth { get => _textDisplayWidth; set { _textDisplayWidth = value; OnPropertyChanged(); } }
    public double TextDisplayHeight { get => _textDisplayHeight; set { _textDisplayHeight = value; OnPropertyChanged(); } }

    /// <summary>The same size at zoom 1 (page units), what hit-testing and the width handle work in.</summary>
    public double TextLayoutWidth { get; set; } = 60;
    public double TextLayoutHeight { get; set; } = 20;

    /// <summary>Em size on screen (font size in page units x zoom) — for the in-place editor.</summary>
    private double _displayFontSize = 16.0;
    public double DisplayFontSize { get => _displayFontSize; set { _displayFontSize = value; OnPropertyChanged(); } }

    /// <summary>Line height on screen — for the in-place editor.</summary>
    public double DisplayLineHeight { get; set; } = 20;

    /// <summary>An opacity mask — a halo round the letters, rotated with the text — that decides where and how strongly the
    /// blurred copy of the page shows through: a halo round the letters themselves (not their bounding box), fading out gradually — see
    /// MainViewModel.CreateHaloMask and TextGeometry.HaloStrength.</summary>
    private Brush? _blurMask;
    public Brush? BlurMask { get => _blurMask; set { _blurMask = value; OnPropertyChanged(); } }

    private Effect? _backdropBlurEffect;
    public Effect? BackdropBlurEffect { get => _backdropBlurEffect; set { _backdropBlurEffect = value; OnPropertyChanged(); } }

    /// <summary>The text box (selection rectangle, text) shows whenever there is a text annotation with a position —
    /// including an empty one being typed into.</summary>
    public bool ShowTextBox => IsText && _vertices.Count > 0;

    /// <summary>The text itself shows only when it has content and the in-place editor isn't showing it.</summary>
    public bool ShowText => IsText && !_isInlineEditing && _text.Length > 0 && _vertices.Count > 0;

    /// <summary>The blurred backdrop: on for this annotation, and there is something to put it behind.</summary>
    public bool ShowBlur => IsText && _blurBackground && _vertices.Count > 0 && (_text.Length > 0 || _isInlineEditing);

    /// <summary>The width handle (right edge of the box) shows while a text annotation is selected and not being typed into.</summary>
    public bool ShowTextHandles => IsText && IsSelected && !_isInlineEditing;

    private void RaiseTextVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowTextBox));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ShowBlur));
        OnPropertyChanged(nameof(ShowTextHandles));
        OnPropertyChanged(nameof(ShowTextOutline));
        OnPropertyChanged(nameof(ShowFrame));
        OnPropertyChanged(nameof(TextSelectionMargin));
        OnPropertyChanged(nameof(TextHandleMargin));
    }

    // =====================================================================================================================
    // Picture (ShapeKind.Image)
    // =====================================================================================================================
    // A picture is placed like text: Vertices[0] is the top-left anchor of its box, the box is rotated clockwise about it, and the whole thing is drawn
    // by the overlay in MainWindow.xaml. ImageWidth/Height are in page units; the Display* sizes are the same at the current zoom. FillOpacity is the
    // picture's opacity (the toolbar's Opacity box), exactly as it is the whole text's.

    public bool IsImage => Kind == ShapeKind.Image;

    /// <summary>The picture's file name inside the job's images folder (see JobImageStore).</summary>
    private string? _imageFile;
    public string? ImageFile { get => _imageFile; set { _imageFile = value; OnPropertyChanged(); } }

    private double _imageWidth;
    /// <summary>The box's size in page units, before rotation.</summary>
    public double ImageWidth { get => _imageWidth; set { _imageWidth = value; OnPropertyChanged(); } }

    private double _imageHeight;
    public double ImageHeight { get => _imageHeight; set { _imageHeight = value; OnPropertyChanged(); } }

    private bool _flipHorizontal;
    public bool FlipHorizontal { get => _flipHorizontal; set { _flipHorizontal = value; OnPropertyChanged(); OnPropertyChanged(nameof(ImageScaleX)); } }

    private bool _flipVertical;
    public bool FlipVertical { get => _flipVertical; set { _flipVertical = value; OnPropertyChanged(); OnPropertyChanged(nameof(ImageScaleY)); } }

    /// <summary>-1 mirrors the picture within its box (a ScaleTransform about the box's centre).</summary>
    public double ImageScaleX => FlipHorizontal ? -1 : 1;
    public double ImageScaleY => FlipVertical ? -1 : 1;

    /// <summary>The decoded picture, shared with the other users of the same file (null if the file is missing or unreadable).</summary>
    private BitmapSource? _imageSource;
    public BitmapSource? ImageSource { get => _imageSource; set { _imageSource = value; OnPropertyChanged(); RaiseImageVisibilityChanged(); } }

    private double _imageDisplayWidth;
    /// <summary>The box's size on screen: the page-unit size at the current zoom.</summary>
    public double ImageDisplayWidth { get => _imageDisplayWidth; set { _imageDisplayWidth = value; OnPropertyChanged(); } }

    private double _imageDisplayHeight;
    public double ImageDisplayHeight { get => _imageDisplayHeight; set { _imageDisplayHeight = value; OnPropertyChanged(); } }

    /// <summary>Opacity of the picture, 0-1, from the toolbar's Opacity box.</summary>
    public double ImageOpacity => IsImage ? FillOpacity / 100.0 : 1.0;

    /// <summary>The picture's box shows whenever there is a picture annotation with a position.</summary>
    public bool ShowImageBox => IsImage && _vertices.Count > 0;

    /// <summary>The picture itself shows when its file could be read.</summary>
    public bool ShowImage => ShowImageBox && _imageSource is not null;

    /// <summary>A grey placeholder shows instead when the file is gone (deleted from the job folder, say): the box stays where it was and can be
    /// moved, resized or deleted like any other.</summary>
    public bool ShowImageMissing => ShowImageBox && _imageSource is null;

    /// <summary>The eight resize handles show for the one picture that is selected and editable.</summary>
    public bool ShowImageHandles => IsImage && IsSelected && IsEditable;

    private void RaiseImageVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowImageBox));
        OnPropertyChanged(nameof(ShowImage));
        OnPropertyChanged(nameof(ShowImageMissing));
        OnPropertyChanged(nameof(ShowImageHandles));
    }

    private List<PolygonVertexViewModel> _vertices = new();
    public List<PolygonVertexViewModel> Vertices
    {
        get => _vertices;
        set { _vertices = value; OnPropertyChanged(); RaiseTextVisibilityChanged(); RaiseImageVisibilityChanged(); }
    }

    /// <summary>Zoom-scaled copy of Vertices, rebuilt via MainViewModel.RefreshPolygonDisplay
    /// whenever Vertices or Zoom change — used only for the vertex-handle Ellipses.</summary>
    private List<PolygonVertexViewModel> _displayVertices = new();
    public List<PolygonVertexViewModel> DisplayVertices
    {
        get => _displayVertices;
        set { _displayVertices = value; OnPropertyChanged(); }
    }

    /// <summary>Zoom-scaled Points for the Polyline — same scaling as DisplayVertices, plus the
    /// closing segment back to the first point when IsClosed.</summary>
    private PointCollection _displayPoints = new();
    public PointCollection DisplayPoints
    {
        get => _displayPoints;
        set { _displayPoints = value; OnPropertyChanged(); }
    }

    private bool _isClosed;
    public bool IsClosed
    {
        get => _isClosed;
        set
        {
            _isClosed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFill));
            OnPropertyChanged(nameof(FillBrush));
        }
    }

    private PolygonPurpose _purpose = PolygonPurpose.None;
    public PolygonPurpose Purpose
    {
        get => _purpose;
        set
        {
            _purpose = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PurposeLabel));
            OnPropertyChanged(nameof(HasPurpose));
            OnPropertyChanged(nameof(ShowPurposeBadge));
            OnPropertyChanged(nameof(IsFrozenRow));
            OnPropertyChanged(nameof(IsFrozenColumn));
            OnPropertyChanged(nameof(IsFrozen));
            OnPropertyChanged(nameof(ShowInScrollableLayer));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(IsNotHidden));
            OnPropertyChanged(nameof(StrokeDashArray));
        }
    }

    public string PurposeLabel => Purpose switch
    {
        PolygonPurpose.FreezeRow => "Freeze Row",
        PolygonPurpose.FreezeColumn => "Freeze Column",
        PolygonPurpose.HideRow => "Hide Row",
        PolygonPurpose.HideColumn => "Hide Column",
        _ => "None",
    };

    /// <summary>True when this polygon has been validated and accepted as a Freeze Row/Column
    /// boundary. Used to show a small badge on the polygon — except for Hide Row/Column, where the
    /// break-line style is already the label (see ShowPurposeBadge).</summary>
    public bool HasPurpose => Purpose != PolygonPurpose.None;

    /// <summary>Whether to show the small text badge (e.g. "Freeze Row") near the polygon. Not
    /// shown for Hide Row/Column — the dashed break-line convention is already self-explanatory,
    /// and a text label would just clutter it.</summary>
    public bool ShowPurposeBadge => HasPurpose && !IsHidden;

    /// <summary>Drives the pinned-while-scrolling overlay's visibility for this polygon.</summary>
    public bool IsFrozenRow => Purpose == PolygonPurpose.FreezeRow;

    /// <summary>Drives the pinned-while-scrolling overlay for a column freeze — mirrors IsFrozenRow,
    /// but the overlay stays fixed horizontally (tracks vertical scroll) instead of vertically.</summary>
    public bool IsFrozenColumn => Purpose == PolygonPurpose.FreezeColumn;

    /// <summary>True for either freeze direction — drives the pinned overlay's overall visibility
    /// (which direction it pins in is decided per-polygon in MainWindow.xaml.cs's
    /// UpdateFrozenOverlayPosition, based on IsFrozenRow vs IsFrozenColumn).</summary>
    public bool IsFrozen => IsFrozenRow || IsFrozenColumn;

    /// <summary>Drives the NORMAL scrollable overlay's visibility for this polygon. Once a polygon
    /// is frozen (either direction), its outline/handles/badge in the scrollable layer are hidden
    /// entirely — the pinned copy (which reuses the same DisplayPoints/StrokeBrush/etc.) is the
    /// only visible representation from that point on, and becomes the interactive one too (see
    /// MainWindow.xaml.cs's FrozenOverlay_MouseLeftButtonDown/RightButtonDown). Unfreezing brings
    /// the normal copy back.</summary>
    public bool ShowInScrollableLayer => !IsFrozen;

    /// <summary>
    /// Screen position (relative to PreviewOuterGrid) of this polygon's pinned overlay, computed
    /// by MainWindow.xaml.cs's UpdateFrozenOverlayPosition. Per-polygon (not a single shared value
    /// on MainViewModel) because a Freeze Row and a Freeze Column polygon need DIFFERENT pin axes
    /// at the same time: Row pins vertically (Y fixed, X tracks horizontal scroll), Column pins
    /// horizontally (X fixed, Y tracks vertical scroll).
    /// </summary>
    private double _frozenOverlayOffsetX;
    public double FrozenOverlayOffsetX
    {
        get => _frozenOverlayOffsetX;
        set { _frozenOverlayOffsetX = value; OnPropertyChanged(); }
    }

    private double _frozenOverlayOffsetY;
    public double FrozenOverlayOffsetY
    {
        get => _frozenOverlayOffsetY;
        set { _frozenOverlayOffsetY = value; OnPropertyChanged(); }
    }

    /// <summary>True for a Hide Row or Hide Column polygon — the underlying content IS actually
    /// concealed (the gap closes; see MainViewModel's RemoveHiddenBands/coordinate mapping). This
    /// just drives the dashed "break line" convention (matching Excel's row/column-hide indicator)
    /// on the outline that remains at the seam.</summary>
    public bool IsHidden => Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn;

    /// <summary>Inverse of IsHidden — drives the normal (non-hidden) outline's visibility. Once
    /// hidden, the polygon's own outline collapses to a degenerate, zero-height/width shape (every
    /// vertex maps to the same seam position), so drawing it directly would trace back-and-forth
    /// over itself. BreakLinePoints (a plain two-point line at the seam) replaces it instead.</summary>
    public bool IsNotHidden => !IsHidden;

    /// <summary>Long dash, gap, short dash, gap, repeating — for a hidden row/column; solid (null)
    /// for everything else. WPF's StrokeDashArray alternates dash-length/gap-length pairs, so this
    /// is (long dash=8, gap=2, short dash=2, gap=2), then repeats.</summary>
    public DoubleCollection? StrokeDashArray => IsHidden ? new DoubleCollection { 8, 2, 2, 2 } : null;

    /// <summary>For a Hide Row/Column polygon only: the two endpoints of a single straight dashed
    /// line at the collapsed seam, spanning the full page width (row) or height (column) — computed
    /// by MainViewModel.RefreshPolygonDisplay. Null for every other polygon.</summary>
    private PointCollection? _breakLinePoints;
    public PointCollection? BreakLinePoints
    {
        get => _breakLinePoints;
        set { _breakLinePoints = value; OnPropertyChanged(); }
    }

    private Color _strokeColor = Colors.Blue;
    public Color StrokeColor
    {
        get => _strokeColor;
        set { _strokeColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(StrokeBrush)); OnPropertyChanged(nameof(EditorForeground)); }
    }

    /// <summary>True by default. Only settable to false when the polygon HasFill (offered as "No
    /// Stroke" in the color combo, but only then — removing the outline of an unfilled polygon
    /// would make it invisible entirely). The original StrokeColor is preserved even while this is
    /// false, so turning the stroke back on later restores it rather than losing it.</summary>
    private bool _hasStroke = true;
    public bool HasStroke
    {
        get => _hasStroke;
        set { _hasStroke = value; OnPropertyChanged(); OnPropertyChanged(nameof(StrokeBrush)); OnPropertyChanged(nameof(ShowTextOutline)); }
    }

    /// <summary>Null (no stroke drawn at all) when HasStroke is false, rather than a transparent
    /// brush — so the outline Polyline doesn't participate in hit-testing/rendering for no reason.</summary>
    public Brush? StrokeBrush => HasStroke ? new SolidColorBrush(StrokeColor) : null;

    private double _strokeThickness = 3.0;
    public double StrokeThickness
    {
        get => _strokeThickness;
        set { _strokeThickness = value; OnPropertyChanged(); }
    }

    /// <summary>Interior fill color — null means no fill at all (the default). Only rendered while
    /// the polygon is closed (see HasFill); an open polyline has no meaningful "interior".</summary>
    private Color? _fillColor;
    public Color? FillColor
    {
        get => _fillColor;
        set
        {
            _fillColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFill));
            OnPropertyChanged(nameof(FillBrush));
            OnPropertyChanged(nameof(TextFillBrush));
            OnPropertyChanged(nameof(EditorForeground));
        }
    }

    /// <summary>Fill opacity as a percentage (0-100), clamped on write. Only meaningful when
    /// FillColor is set.</summary>
    private double _fillOpacity = 50.0;
    public double FillOpacity
    {
        get => _fillOpacity;
        set { _fillOpacity = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(FillBrush)); OnPropertyChanged(nameof(TextOpacity)); OnPropertyChanged(nameof(ImageOpacity)); }
    }

    /// <summary>False by default (opt-in). When true, this polygon's outline/fill is drawn directly
    /// onto the actual printed/exported page — everything else about a polygon is purely a preview/
    /// editing aid that never appears in the output. Currently only honored for "Print Whole Page"/
    /// whole-page export, not split tiles — see MainViewModel.DrawIncludedPolygonsOntoPage's docs.</summary>
    private bool _includeInPrint;
    public bool IncludeInPrint
    {
        get => _includeInPrint;
        set { _includeInPrint = value; OnPropertyChanged(); }
    }

    /// <summary>Whether there is a fill to show: a closed shape with a fill colour — or text with one (its letters' colour; text
    /// is never "closed", but a fill is exactly what colours it).</summary>
    public bool HasFill => (IsClosed || IsText) && FillColor.HasValue;

    /// <summary>The actual brush used for the interior fill, with FillOpacity baked into its alpha
    /// channel — null (no fill drawn at all) when HasFill is false, rather than a fully-transparent
    /// brush, so the fill Polygon element doesn't participate in hit-testing/rendering for no reason.</summary>
    public Brush? FillBrush => HasFill
        ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(FillOpacity / 100.0 * 255), FillColor!.Value.R, FillColor.Value.G, FillColor.Value.B))
        : null;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowVertexHandles));
            OnPropertyChanged(nameof(SelectionEffect));
            OnPropertyChanged(nameof(ShowTextHandles));
            RaiseImageVisibilityChanged();
        }
    }

    /// <summary>A black glow around the outline when selected. Replaces the earlier approach of
    /// simply doubling the line thickness, which looked heavy-handed. Kept deliberately subtle
    /// (small blur radius, modest opacity) — a strong blur bleeds into the line itself and fades
    /// its own color, which defeats the point of a "make it bolder" indicator. Null (no effect at
    /// all) when not selected.</summary>
    public Effect? SelectionEffect => IsSelected
        ? new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 4, Opacity = 0.6 }
        : null;


    private bool _isEditable;
    /// <summary>True only for the current EditablePolygon (see MainViewModel) — the polygon being
    /// freshly drawn, or the sole selected one when exactly one is selected. Drag/insert/delete
    /// only ever act on this one polygon.</summary>
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            _isEditable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowVertexHandles));
            RaiseImageVisibilityChanged();
        }
    }

    /// <summary>Whether to show this polygon's round vertex markers. Shown for the editable
    /// polygon (so it can be dragged/edited) AND for every other selected polygon in a
    /// multi-selection (so the selection is visibly confirmed, even though only the editable one
    /// can actually be dragged).</summary>
    public bool ShowVertexHandles => (IsSelected || IsEditable) && !IsText && !IsImage; // a text label or a picture is moved by dragging its box, so it has no point handle

    /// <summary>
    /// For a Freeze Row polygon: the (zoom-scaled) clip region the pinned-while-scrolling overlay
    /// will use to show exactly this polygon's shape — computed by MainViewModel.RefreshPolygonDisplay
    /// from the polygon's vertices, extended to the full page width first if needed (see
    /// MainViewModel.TryAssignFreezeRowPurpose/ExtendPolygonToFullWidth — the ORIGINAL Vertices are
    /// never modified for this). Null for polygons with no freeze purpose, or before the actual
    /// pinned-scroll rendering (a separate follow-up increment) consumes it.
    /// </summary>
    private Geometry? _freezeClipGeometry;
    public Geometry? FreezeClipGeometry
    {
        get => _freezeClipGeometry;
        set { _freezeClipGeometry = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One selectable color swatch for the polygon-color combo box. IsNoFill marks the
/// special "no fill" entry in the fill-color combo specifically (unused/false for the stroke-color
/// combo, which has no such option since a polygon always has some stroke color).</summary>
/// <summary>One selectable color swatch for the polygon-color combo box. IsNoFill marks the
/// special "no fill" entry in the fill-color combo; IsNoStroke marks the special "no stroke" entry
/// in the stroke-color combo (only offered when the target polygon HasFill — see
/// MainViewModel.AvailableStrokeColorOptions). A given option is at most one of these.</summary>
public sealed class ColorOption
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public bool IsNoFill { get; init; }
    public bool IsNoStroke { get; init; }
    public Brush Brush => new SolidColorBrush(Color);

    /// <summary>A darker shade of this option's own Color, used for the swatch's border (and, for
    /// No Fill/No Stroke specifically, the X mark too) — so each swatch's border relates visually
    /// to its own color instead of a uniform black that doesn't read well against every color.
    /// For No Fill/No Stroke (Color = White), this comes out as a mid-gray.</summary>
    public Brush BorderBrush => new SolidColorBrush(Color.FromRgb(
        (byte)(Color.R * 0.55), (byte)(Color.G * 0.55), (byte)(Color.B * 0.55)));
}

/// <summary>One entry of the frame's line-style box: the style, its name, and the pattern to draw its sample line with (in multiples of the line's width).</summary>
public sealed class FrameLineStyleOption
{
    public FrameLineStyleOption(FrameLineStyle style)
    {
        Style = style;
        Name = FrameLineStyles.Label(style);
        DashArray = new DoubleCollection(FrameLineStyles.DashArray(style));
    }

    public FrameLineStyle Style { get; }
    public string Name { get; }
    public DoubleCollection DashArray { get; }
}
