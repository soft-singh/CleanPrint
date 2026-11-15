using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CleanPrint.App.Controls;
using CleanPrint.App.Services;
using CleanPrint.Core.Models;

namespace CleanPrint.App;

/// <summary>
/// "Edit Base Image": turn the page by a precise angle, crop it by dragging handles (or typing the numbers), make the canvas bigger, and apply effects from the same Effects panel the
/// main window has — then Save writes the result over the page's own image, for good. Until then nothing is touched: everything is previewed, and Undo / Redo walk back and forth
/// through what has been done. The geometry is BaseImageEditMath (Core, tested); the pixel work is BaseImageEditService; what happens to the shapes on the page afterwards is
/// MainViewModel.ApplyBaseImageEditAsync.
///
/// TOOLS. Crop (the default), Erase (paint areas white: a rectangle, or an eraser brush), Straighten (draw a line along something that should be level or upright and the page is turned to
/// make it so), and Skew (drag four corners onto a page photographed at an angle and it is stretched to a true rectangle). They work in the order the editor applies them:
/// effects, then skew correction, then the turn, then the crop, then the erased areas are painted white — each in the coordinates of the step before it, so a change to an earlier step
/// (turning, skewing) clears the erased areas, which were drawn on the page as it was (Undo brings them back).
///
/// What is shown is what is saved, in the same order: effects first, then skew, the turn, the crop and the erasing. The preview works on a reduced copy of the page (so dragging stays quick on a big
/// scan; zooming in swaps in a sharper copy); Save redoes it all at full resolution.
///
/// THE STAGE. The turned canvas is drawn in the middle of a grey PASTEBOARD, inside a scrolling view that can be zoomed. The crop rectangle is not confined to the canvas: dragged out
/// into the pasteboard (or given larger numbers, or "Add white margin"), it makes the saved page bigger than the image, the extra being white. That is how the canvas is enlarged.
/// All the pieces on the stage are placed in CANVAS PIXELS (full-resolution pixels of the turned canvas, with (0,0) at its top-left) and converted to screen units with
/// <see cref="ScreenX"/>/<see cref="ScreenY"/>; <c>_stage</c> is the region of canvas pixels the stage draws.
/// </summary>
public partial class BaseImageEditorWindow : Window
{
    private const int StagePad = 18;                       // screen units of empty space kept around the stage
    private const double ZoomStep = 1.25;
    private const double MinScale = 0.02, MaxScale = 16;   // screen units per canvas pixel
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(600);

    private readonly BaseImageEditContext _context;
    private readonly CapturedPage _raw;
    private CapturedPage _previewBase;                     // the reduced copy that is previewed; swapped for a sharper one when zoomed in
    private int _previewDimension;
    private readonly ImageEffectSettings _settings = ImageEffectSettings.NoEffects();   // the editor starts from "no effects": opening and saving only turns/crops
    private readonly long _maxPixels;

    private double _angle;
    private PixelRect _crop;
    private int _canvasWidth, _canvasHeight;               // the turned canvas, in full-resolution pixels (of the raw image itself while the Skew tool is open)

    // tools
    private enum Tool { Crop, Erase, Straighten, Skew }
    private Tool _tool = Tool.Crop;

    private Quad? _skewQuad;                               // the skew correction that has been applied (null = none)
    private PerspectiveStage? _skew;                       // …and what it makes of the page
    private Quad _skewWork;                                // the corners being dragged while the Skew tool is open
    private int _skewDrag = -1;
    private int _baseWidth, _baseHeight;                   // the image the turn starts from: the raw image, or its skew-corrected version
    private IReadOnlyList<EraseShape> _erasures = Array.Empty<EraseShape>();
    private BitmapSource? _unwarpedBitmap, _warpedBitmap;  // the preview with effects, as photographed and as corrected
    private string _note = "";                             // a one-off message appended to the info line (e.g. that erasures were cleared)

    // erase / straighten gestures
    private bool _toolDragging;
    private Point _gestureStart;
    private List<GeoPoint> _brushPoints = new();
    private UIElement? _liveBrush;
    private PixelRect _stage;                              // the part of the plane (canvas + pasteboard, or more) that is drawn, in canvas pixels
    private double _fitScale = 1, _zoom = 1, _scale = 1;   // _scale = screen units per canvas pixel = _fitScale * _zoom
    private string _limitNote = "";

    private readonly Rectangle[] _handles = new Rectangle[8];   // the crop's, indexed by (int)ImageHandle
    private readonly Rectangle[] _quadHandles = new Rectangle[4];   // the skew corners: top-left, top-right, bottom-right, bottom-left
    private bool _ready, _applying, _updatingFields, _busy;

    // mouse: the crop
    private enum DragKind { None, Move, Resize, NewCrop }
    private DragKind _drag;
    private ImageHandle _dragHandle;
    private Point _dragStart;
    private PixelRect _dragStartCrop;

    // mouse: panning
    private bool _panning;
    private Point _panStart;
    private double _panStartHorizontal, _panStartVertical;

    // timers
    private readonly DispatcherTimer _effectsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _historyTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _resolutionTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _effectsCts;

    // undo / redo
    private sealed record EditState(double Angle, PixelRect Crop, ImageEffectSettings Effects, Quad? Skew, IReadOnlyList<EraseShape> Erasures);
    private readonly List<EditState> _history = new();
    private int _historyIndex;
    private DateTime _lastRecordUtc = DateTime.MinValue;
    private bool _lastRecordCoalescible;
    private const int MaxHistory = 100;

    /// <summary>Set when Save succeeds: the finished page and what was done to it. Null if the dialog was cancelled.</summary>
    public BaseImageEditResult? Result { get; private set; }

    public BaseImageEditorWindow(BaseImageEditContext context)
    {
        InitializeComponent();
        _context = context;
        _raw = context.RawPage;
        _maxPixels = BaseImageEditMath.MaxOutputPixels(_raw.Width, _raw.Height);
        _previewDimension = Math.Min(1600, Math.Max(_raw.Width, _raw.Height));
        _previewBase = BaseImageEditService.Downscale(_raw, _previewDimension);
        _baseWidth = _raw.Width;
        _baseHeight = _raw.Height;
        BrushSizeBox.Value = Math.Clamp(Math.Max(_raw.Width, _raw.Height) / 50, 20, 400);   // a brush about 2% of the page: big enough to be useful at once
        Title = $"Edit Base Image — page {context.PageNumber} of {context.PageCount}";

        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };
            Stage.Children.Add(_handles[i]);
        }

        // The same Effects panel as the main window — with its own settings, so nothing here touches the main window until Save.
        var panel = new EffectsPanel(_settings);
        panel.HideAutoCropSection();
        panel.HideDockToggle();
        panel.SetDockedState(false);
        EffectsHost.Content = panel;

        for (int i = 0; i < _quadHandles.Length; i++)
        {
            _quadHandles[i] = new Rectangle { Width = 13, Height = 13, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x00)), Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Stage.Children.Add(_quadHandles[i]);
        }

        _unwarpedBitmap = _warpedBitmap = BaseImageEditService.ToBitmap(_previewBase);
        PreviewImage.Source = _warpedBitmap;
        ToolHint.Text = ToolHintFor(Tool.Crop);

        // The number box has no change event of its own, so watch its Value property.
        var valueProperty = DependencyPropertyDescriptor.FromProperty(NumericUpDown.ValueProperty, typeof(NumericUpDown));
        valueProperty.AddValueChanged(AngleBox, (_, _) => AngleBox_Changed());
        foreach (var box in new[] { CropLeftBox, CropTopBox, CropWidthBox, CropHeightBox })
            valueProperty.AddValueChanged(box, (_, _) => CropBox_Changed());

        _settings.PropertyChanged += (_, _) => OnSettingsChanged();
        _settings.BulkChanged += (_, _) => OnSettingsChanged();
        _effectsTimer.Tick += async (_, _) => { _effectsTimer.Stop(); await RefreshPreviewAsync(); };
        _historyTimer.Tick += (_, _) => { _historyTimer.Stop(); RecordHistory(coalesce: true); };
        _resolutionTimer.Tick += async (_, _) => { _resolutionTimer.Stop(); await EnsurePreviewResolutionAsync(); };

        Loaded += (_, _) =>
        {
            _ready = true;
            RelayoutStage(resetCrop: true);
            _history.Add(CurrentState());
            _historyIndex = 0;
            UpdateButtons();
        };
    }

    /// <summary>Opens filling the usable height of the screen it is on (the authored size in the XAML is only a ceiling), so Save and Cancel are never below the bottom edge. With a
    /// larger "Toolbar scale" the window had been made bigger still, and on a smaller screen it simply ran off it.</summary>
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // No MaxWidth/MaxHeight here on purpose: a maximized WPF window is a few units LARGER than the work area (its invisible resize borders hang off the screen edge),
        // so capping the size at the work area leaves a black strip along the right and bottom when it is maximized. The starting size below is all that is needed.
        var work = MonitorWorkArea.For(Owner);
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Min(Width, work.Width);
        Height = work.Height;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top;
    }

    // =================================================================================================================
    // Layout: the turned canvas on its pasteboard, fitted or zoomed, with the crop over it
    // =================================================================================================================

    private double ScreenX(double canvasX) => (canvasX - _stage.X) * _scale;
    private double ScreenY(double canvasY) => (canvasY - _stage.Y) * _scale;
    private double CanvasX(double stageX) => stageX / _scale + _stage.X;
    private double CanvasY(double stageY) => stageY / _scale + _stage.Y;

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) RelayoutStage(resetCrop: false);
    }

    /// <summary>Works out the turned canvas, which part of the plane to draw, and how big, and places everything. With resetCrop the crop goes back to the whole canvas — needed whenever
    /// the angle changes, since a crop is a rectangle OF the turned canvas and the canvas just changed shape.</summary>
    private void RelayoutStage(bool resetCrop)
    {
        var (baseWidth, baseHeight, angle) = ViewBase;
        (_canvasWidth, _canvasHeight) = BaseImageEditMath.RotatedCanvasSize(baseWidth, baseHeight, angle);
        if (_tool == Tool.Skew)
        {
            // No crop to take in: the image and its margin — and wherever the corners have been dragged to, which may be well outside the image (the part of the corrected page that
            // falls outside the photograph is simply white), so the stage grows to hold them, just as it does for a crop reaching past the page.
            _stage = BaseImageEditMath.StageRect(_canvasWidth, _canvasHeight, QuadBounds(_skewWork));
        }
        else
        {
            if (resetCrop || _crop.Width <= 0) _crop = new PixelRect(0, 0, _canvasWidth, _canvasHeight);
            _crop = BaseImageEditMath.ClampCrop(_crop, _canvasWidth, _canvasHeight, _maxPixels);
            _stage = BaseImageEditMath.StageRect(_canvasWidth, _canvasHeight, _crop);
        }
        ApplyScale();
        UpdateOverlays();
        RequestPreviewResolution();
    }

    /// <summary>What the stage is drawing the page as: normally the (skew-corrected) page turned by the Angle; while the Skew tool is open, the page AS PHOTOGRAPHED, unturned, so the corners
    /// can be put on it.</summary>
    private (int Width, int Height, double Angle) ViewBase => _tool == Tool.Skew ? (_raw.Width, _raw.Height, 0.0) : (_baseWidth, _baseHeight, _angle);

    /// <summary>Shows the preview picture that goes with the current tool (as photographed for Skew, otherwise skew-corrected) — unless it doesn't match the shape the stage is laying out (a
    /// correction was just applied and the new picture is still being made), in which case it is hidden rather than shown stretched.</summary>
    private void ShowCurrentBitmap()
    {
        var bitmap = _tool == Tool.Skew ? _unwarpedBitmap : _warpedBitmap;
        PreviewImage.Source = bitmap;
        var (baseWidth, baseHeight, _) = ViewBase;
        bool matches = bitmap is not null && baseWidth > 0 && baseHeight > 0 &&
                       Math.Abs((double)bitmap.PixelWidth / bitmap.PixelHeight - (double)baseWidth / baseHeight) < 0.03;
        PreviewImage.Visibility = matches ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Sets the scale (fit-to-view times the zoom) and sizes everything that depends on it.</summary>
    private void ApplyScale()
    {
        double availableWidth = Math.Max(60, Viewport.ActualWidth - 2 * StagePad), availableHeight = Math.Max(60, Viewport.ActualHeight - 2 * StagePad);
        _fitScale = Math.Min(availableWidth / _stage.Width, availableHeight / _stage.Height);
        _scale = Math.Clamp(_fitScale * _zoom, MinScale, MaxScale);
        _zoom = _scale / _fitScale;

        Stage.Width = _stage.Width * _scale;
        Stage.Height = _stage.Height * _scale;
        double scrollWidth = ViewScroll.ViewportWidth > 0 ? ViewScroll.ViewportWidth : Math.Max(0, Viewport.ActualWidth - 8);
        double scrollHeight = ViewScroll.ViewportHeight > 0 ? ViewScroll.ViewportHeight : Math.Max(0, Viewport.ActualHeight - 8);
        StageHost.Width = Math.Max(Stage.Width + 2 * StagePad, scrollWidth);
        StageHost.Height = Math.Max(Stage.Height + 2 * StagePad, scrollHeight);

        // The canvas, white, on the pasteboard.
        Canvas.SetLeft(CanvasBackground, ScreenX(0));
        Canvas.SetTop(CanvasBackground, ScreenY(0));
        CanvasBackground.Width = _canvasWidth * _scale;
        CanvasBackground.Height = _canvasHeight * _scale;

        // The page itself, in the middle of the canvas and turned about its own centre — which is the canvas's centre too, as BaseImageEditMath.MapPixel has it.
        var (viewWidth, viewHeight, viewAngle) = ViewBase;
        PreviewImage.Width = viewWidth * _scale;
        PreviewImage.Height = viewHeight * _scale;
        Canvas.SetLeft(PreviewImage, ScreenX(_canvasWidth / 2.0) - PreviewImage.Width / 2);
        Canvas.SetTop(PreviewImage, ScreenY(_canvasHeight / 2.0) - PreviewImage.Height / 2);
        PreviewImage.RenderTransform = new RotateTransform(viewAngle);
        ShowCurrentBitmap();

        ZoomText.Text = $"{_scale * 100:0}%";
    }

    private void SetCrop(PixelRect requested, bool relayout)
    {
        var clamped = BaseImageEditMath.ClampCrop(requested, _canvasWidth, _canvasHeight, _maxPixels);
        _limitNote = (long)Math.Max(1, requested.Width) * Math.Max(1, requested.Height) > _maxPixels
            ? $" Limited to {_maxPixels / 1_000_000} megapixels." : "";
        _crop = clamped;
        if (relayout) RelayoutStage(resetCrop: false); else UpdateCropVisuals();
    }

    /// <summary>Everything drawn over the page that belongs to the current tool: the crop and its handles, the erased areas, the skew corners.</summary>
    private void UpdateOverlays()
    {
        UpdateCropVisuals();
        RebuildEraseLayer();
        UpdateQuadVisuals();
    }

    private void UpdateCropVisuals()
    {
        // The crop is only shown (and only means anything) while the page is being turned and cropped, not while the corners of a skew correction are being placed.
        var cropVisibility = _tool == Tool.Skew ? Visibility.Collapsed : Visibility.Visible;
        CropBackground.Visibility = Dimmer.Visibility = CropBorder.Visibility = CropBorderDark.Visibility = cropVisibility;
        foreach (var handle in _handles) handle.Visibility = cropVisibility;
        if (_tool == Tool.Skew)
        {
            InfoText.Text = "Drag each yellow corner onto the matching corner of the page — they may go outside the photograph, into the grey, and what falls outside becomes white. Then press Apply skew correction.";
            return;
        }

        double x = ScreenX(_crop.X), y = ScreenY(_crop.Y), w = _crop.Width * _scale, h = _crop.Height * _scale;

        // White behind the whole crop, so a part of it that reaches past the canvas shows as the white it will be.
        Canvas.SetLeft(CropBackground, x); Canvas.SetTop(CropBackground, y);
        CropBackground.Width = w; CropBackground.Height = h;

        Dimmer.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Stage.Width, Stage.Height)), new RectangleGeometry(new Rect(x, y, w, h)));
        foreach (var border in new[] { CropBorder, CropBorderDark })
        {
            Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
            border.Width = w; border.Height = h;
        }
        for (int i = 0; i < _handles.Length; i++)
        {
            var p = ImageGeometry.HandlePosition((ImageHandle)i, w, h);
            Canvas.SetLeft(_handles[i], x + p.X - _handles[i].Width / 2);
            Canvas.SetTop(_handles[i], y + p.Y - _handles[i].Height / 2);
        }

        _updatingFields = true;
        CropLeftBox.Value = _crop.X; CropTopBox.Value = _crop.Y; CropWidthBox.Value = _crop.Width; CropHeightBox.Value = _crop.Height;
        _updatingFields = false;

        double inchesWide = _crop.Width / (double)Math.Max(1, _raw.Dpi), inchesHigh = _crop.Height / (double)Math.Max(1, _raw.Dpi);
        bool bigger = _crop.X < 0 || _crop.Y < 0 || _crop.Right > _canvasWidth || _crop.Bottom > _canvasHeight;
        InfoText.Text = $"The saved page will be {_crop.Width:N0} × {_crop.Height:N0} pixels at {_raw.Dpi} DPI ({inchesWide * 25.4:0} × {inchesHigh * 25.4:0} mm)." +
                        (bigger ? " It reaches past the page, so the extra is white." : "") + _limitNote + _note;
    }

    // =================================================================================================================
    // Zoom and pan
    // =================================================================================================================

    /// <summary>Zooms to a multiple of "fit", keeping the point of the page under <paramref name="viewAnchor"/> (a point in the scroll view) where it is.</summary>
    private void ZoomTo(double newZoom, Point viewAnchor)
    {
        // The canvas pixel under the anchor now…
        double hostX = ViewScroll.HorizontalOffset + viewAnchor.X, hostY = ViewScroll.VerticalOffset + viewAnchor.Y;
        double canvasX = CanvasX(hostX - (StageHost.Width - Stage.Width) / 2), canvasY = CanvasY(hostY - (StageHost.Height - Stage.Height) / 2);

        _zoom = newZoom;
        ApplyScale();
        UpdateOverlays();
        ViewScroll.UpdateLayout();

        // …must be under it after.
        double newHostX = (StageHost.Width - Stage.Width) / 2 + ScreenX(canvasX), newHostY = (StageHost.Height - Stage.Height) / 2 + ScreenY(canvasY);
        ViewScroll.ScrollToHorizontalOffset(newHostX - viewAnchor.X);
        ViewScroll.ScrollToVerticalOffset(newHostY - viewAnchor.Y);
        RequestPreviewResolution();
    }

    private Point ViewCentre => new(ViewScroll.ViewportWidth / 2, ViewScroll.ViewportHeight / 2);

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomTo(_zoom * ZoomStep, ViewCentre);
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomTo(_zoom / ZoomStep, ViewCentre);
    private void Zoom100Button_Click(object sender, RoutedEventArgs e) => ZoomTo(1.0 / _fitScale, ViewCentre);   // one screen unit per image pixel

    private void ZoomFitButton_Click(object sender, RoutedEventArgs e)
    {
        _zoom = 1;
        ApplyScale();
        UpdateOverlays();
        ViewScroll.UpdateLayout();
        ViewScroll.ScrollToHome();
        RequestPreviewResolution();
    }

    /// <summary>Plain wheel scrolls (left to the ScrollViewer), Shift + wheel scrolls sideways, Ctrl + wheel zooms at the pointer — the same as the main window's preview.</summary>
    private void ViewScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_busy) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomTo(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), e.GetPosition(ViewScroll));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            ViewScroll.ScrollToHorizontalOffset(ViewScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static bool IsWithinScrollBar(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    /// <summary>The middle button, Space + the left button, or the left button while the Pan switch is on, picks the page up and drags it. Seen here first (this event tunnels down
    /// through the scroll view before it reaches the stage), so panning takes the click instead of the crop; the scroll bars are left alone.</summary>
    private void ViewScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || IsWithinScrollBar(e.OriginalSource as DependencyObject)) return;
        bool middle = e.ChangedButton == MouseButton.Middle;
        bool leftPan = e.ChangedButton == MouseButton.Left && (PanToggle.IsChecked == true || Keyboard.IsKeyDown(Key.Space));
        if (!middle && !leftPan) return;

        Keyboard.ClearFocus();
        _panning = true;
        _panStart = e.GetPosition(ViewScroll);
        _panStartHorizontal = ViewScroll.HorizontalOffset;
        _panStartVertical = ViewScroll.VerticalOffset;
        ViewScroll.CaptureMouse();
        ViewScroll.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void ViewScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var now = e.GetPosition(ViewScroll);
        ViewScroll.ScrollToHorizontalOffset(_panStartHorizontal - (now.X - _panStart.X));
        ViewScroll.ScrollToVerticalOffset(_panStartVertical - (now.Y - _panStart.Y));
        e.Handled = true;
    }

    private void ViewScroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ViewScroll.ReleaseMouseCapture();
        ViewScroll.Cursor = null;
        e.Handled = true;
    }

    // ---- the preview's own resolution

    private void RequestPreviewResolution()
    {
        _resolutionTimer.Stop();
        _resolutionTimer.Start();
    }

    /// <summary>Zoomed in, the preview must not be a blown-up thumbnail: once the page is shown larger than the preview copy has pixels for, a sharper copy is made (up to the whole page).
    /// Back out, it goes back to a small one. See BaseImageEditMath.PreviewDimension for the steps.</summary>
    private async Task EnsurePreviewResolutionAsync()
    {
        int longest = Math.Max(_raw.Width, _raw.Height);
        double shown = longest * _scale * 1.25;     // screen pixels the page's longer side takes (allowing for a 125% display)
        int wanted = BaseImageEditMath.PreviewDimension(shown, longest);
        if (wanted == _previewDimension) return;

        _previewDimension = wanted;
        _previewBase = await Task.Run(() => BaseImageEditService.Downscale(_raw, wanted));
        await RefreshPreviewAsync();
    }

    // =================================================================================================================
    // The crop: drag a handle, drag inside to move it, drag outside to draw a new one — or type the numbers. It may reach out past the page.
    // =================================================================================================================

    private void Stage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        Keyboard.ClearFocus(); // so Ctrl+Z reaches the editor, not a number box that was last clicked into
        _note = "";
        var p = ClampToStage(e.GetPosition(Stage));

        if (_tool != Tool.Crop)
        {
            BeginToolGesture(p);
            if (_toolDragging) { Stage.CaptureMouse(); e.Handled = true; }
            return;
        }

        double x = ScreenX(_crop.X), y = ScreenY(_crop.Y), w = _crop.Width * _scale, h = _crop.Height * _scale;

        if (ImageGeometry.HitHandle(new GeoPoint(p.X - x, p.Y - y), w, h, 10) is { } handle)
        {
            _drag = DragKind.Resize;
            _dragHandle = handle;
        }
        else if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h)
        {
            _drag = DragKind.Move;
        }
        else
        {
            _drag = DragKind.NewCrop;
        }

        _dragStart = p;
        _dragStartCrop = _crop;
        Stage.CaptureMouse();
        e.Handled = true;
    }

    private void Stage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var p = ClampToStage(e.GetPosition(Stage));

        if (_tool != Tool.Crop)
        {
            if (_toolDragging && e.LeftButton == MouseButtonState.Pressed) ContinueToolGesture(p);
            else if (!_toolDragging) Stage.Cursor = PanToggle.IsChecked == true || Keyboard.IsKeyDown(Key.Space) ? Cursors.Hand : _tool == Tool.Skew && HitQuadHandle(p) < 0 ? Cursors.Arrow : Cursors.Cross;
            return;
        }

        if (_drag == DragKind.None)
        {
            double x = ScreenX(_crop.X), y = ScreenY(_crop.Y), w = _crop.Width * _scale, h = _crop.Height * _scale;
            Stage.Cursor = PanToggle.IsChecked == true || Keyboard.IsKeyDown(Key.Space) ? Cursors.Hand
                : ImageGeometry.HitHandle(new GeoPoint(p.X - x, p.Y - y), w, h, 10) is { } hover ? CursorForHandle(hover)
                : p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h ? Cursors.SizeAll
                : Cursors.Cross;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        double startX = ScreenX(_dragStartCrop.X), startY = ScreenY(_dragStartCrop.Y), startW = _dragStartCrop.Width * _scale, startH = _dragStartCrop.Height * _scale;
        switch (_drag)
        {
            case DragKind.Resize:
            {
                // The same handle maths as a picture's box (ImageGeometry.Resize, tested), unrotated, in screen units, then back to pixels.
                var (anchor, width, height) = ImageGeometry.Resize(new GeoPoint(startX, startY), 0, startW, startH, _dragHandle,
                    new GeoPoint(p.X, p.Y), KeepProportionsCheck.IsChecked == true, minSize: Math.Max(4, 8 * _scale));
                SetCrop(new PixelRect((int)Math.Round(CanvasX(anchor.X)), (int)Math.Round(CanvasY(anchor.Y)),
                    (int)Math.Round(width / _scale), (int)Math.Round(height / _scale)), relayout: false);
                break;
            }
            case DragKind.Move:
            {
                int dx = (int)Math.Round((p.X - _dragStart.X) / _scale), dy = (int)Math.Round((p.Y - _dragStart.Y) / _scale);
                SetCrop(new PixelRect(
                    Math.Clamp(_dragStartCrop.X + dx, _stage.X, Math.Max(_stage.X, _stage.Right - _dragStartCrop.Width)),
                    Math.Clamp(_dragStartCrop.Y + dy, _stage.Y, Math.Max(_stage.Y, _stage.Bottom - _dragStartCrop.Height)),
                    _dragStartCrop.Width, _dragStartCrop.Height), relayout: false);
                break;
            }
            case DragKind.NewCrop:
            {
                double left = Math.Min(_dragStart.X, p.X), top = Math.Min(_dragStart.Y, p.Y), right = Math.Max(_dragStart.X, p.X), bottom = Math.Max(_dragStart.Y, p.Y);
                SetCrop(new PixelRect((int)Math.Round(CanvasX(left)), (int)Math.Round(CanvasY(top)),
                    Math.Max(1, (int)Math.Round((right - left) / _scale)), Math.Max(1, (int)Math.Round((bottom - top) / _scale))), relayout: false);
                break;
            }
        }
    }

    private void Stage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_toolDragging)
        {
            _toolDragging = false;
            Stage.ReleaseMouseCapture();
            EndToolGesture(ClampToStage(e.GetPosition(Stage)));
            return;
        }
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        Stage.ReleaseMouseCapture();
        RelayoutStage(resetCrop: false);      // the stage may need to shrink back (or grow) to fit what the crop now covers
        RecordHistory(coalesce: false);
    }

    private Point ClampToStage(Point p) => new(Math.Clamp(p.X, 0, Stage.Width), Math.Clamp(p.Y, 0, Stage.Height));

    /// <summary>The double-headed arrow along a handle's direction (the crop is never turned, so no adjustment is needed).</summary>
    private static Cursor CursorForHandle(ImageHandle handle) => handle switch
    {
        ImageHandle.Left or ImageHandle.Right => Cursors.SizeWE,
        ImageHandle.Top or ImageHandle.Bottom => Cursors.SizeNS,
        ImageHandle.TopLeft or ImageHandle.BottomRight => Cursors.SizeNWSE,
        _ => Cursors.SizeNESW,
    };

    private void CropBox_Changed()
    {
        if (_updatingFields || !_ready || _tool == Tool.Skew) return;
        SetCrop(new PixelRect((int)Math.Round(CropLeftBox.Value), (int)Math.Round(CropTopBox.Value), (int)Math.Round(CropWidthBox.Value), (int)Math.Round(CropHeightBox.Value)),
            relayout: true);
        RecordHistory(coalesce: true);
    }

    private void ResetCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tool == Tool.Skew) return;
        SetCrop(new PixelRect(0, 0, _canvasWidth, _canvasHeight), relayout: true);
        RecordHistory(coalesce: false);
    }

    /// <summary>Makes the canvas bigger: the crop is widened by the margins typed in, on each side, and what it now covers beyond the page is white.</summary>
    private void AddMarginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tool == Tool.Skew) return;
        int left = (int)Math.Round(MarginLeftBox.Value), top = (int)Math.Round(MarginTopBox.Value);
        int right = (int)Math.Round(MarginRightBox.Value), bottom = (int)Math.Round(MarginBottomBox.Value);
        if (left + top + right + bottom <= 0) return;

        SetCrop(new PixelRect(_crop.X - left, _crop.Y - top, _crop.Width + left + right, _crop.Height + top + bottom), relayout: true);
        MarginLeftBox.Value = MarginTopBox.Value = MarginRightBox.Value = MarginBottomBox.Value = 0;
        RecordHistory(coalesce: false);
    }

    // =================================================================================================================
    // The angle
    // =================================================================================================================

    private void AngleBox_Changed()
    {
        if (_updatingFields || !_ready || _tool == Tool.Skew) return;
        _angle = BaseImageEditMath.NormalizeAngle(AngleBox.Value);
        ClearErasuresBecauseThePageChanged("turned");
        RelayoutStage(resetCrop: true);
        RecordHistory(coalesce: true);
    }

    /// <summary>Turns the page to an angle (from the 90 degree buttons, or the Straighten tool).</summary>
    private void TurnTo(double degrees)
    {
        _angle = BaseImageEditMath.NormalizeAngle(degrees);
        _updatingFields = true;
        AngleBox.Value = _angle;
        _updatingFields = false;
        ClearErasuresBecauseThePageChanged("turned");
        RelayoutStage(resetCrop: true);
        RecordHistory(coalesce: false);
    }

    private void TurnBy(double degrees) => TurnTo(_angle + degrees);

    /// <summary>The erased areas were drawn on the page as it was, so turning or skewing it leaves them in the wrong place: they are cleared (Undo brings them back), and the info line says so.</summary>
    private void ClearErasuresBecauseThePageChanged(string what)
    {
        if (_erasures.Count == 0) return;
        _erasures = Array.Empty<EraseShape>();
        _note = $" The erased areas were cleared because the page was {what} (Undo brings them back).";
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => TurnBy(-90);
    private void RotateRightButton_Click(object sender, RoutedEventArgs e) => TurnBy(90);

    // =================================================================================================================
    // Effects preview
    // =================================================================================================================

    private void OnSettingsChanged()
    {
        _effectsTimer.Stop(); _effectsTimer.Start();                 // re-render once the sliders pause
        if (!_applying) { _historyTimer.Stop(); _historyTimer.Start(); }
    }

    /// <summary>
    /// Makes the preview pictures: the effects applied to the preview copy, and — if a skew correction has been applied — that warped to a true rectangle (the same warp the saved page gets,
    /// at the preview's scale). A newer request cancels an older one that is still working.
    /// </summary>
    private async Task RefreshPreviewAsync()
    {
        _effectsCts?.Cancel();
        var cts = _effectsCts = new CancellationTokenSource();
        var working = _settings.Clone();
        working.AutoCropEnabled = false;   // the crop here is the one drawn by hand
        var basis = _previewBase;
        var quad = _skewQuad;
        double factor = (double)basis.Width / _raw.Width;   // how much smaller the preview copy is than the page

        try
        {
            var processed = working.ChangesPixels ? await _context.Processing.ApplyEffectsAsync(basis, working, cts.Token) : basis;
            if (cts.IsCancellationRequested) return;
            var unwarped = BaseImageEditService.ToBitmap(processed);

            BitmapSource warped = unwarped;
            if (quad is { } q)
            {
                var full = PerspectiveStage.From(q);
                int width = Math.Max(1, (int)Math.Round(full.Width * factor)), height = Math.Max(1, (int)Math.Round(full.Height * factor));
                var homography = PerspectiveMath.Homography(q.Scaled(factor), width, height);
                var corrected = await Task.Run(() => BaseImageEditService.WarpPerspective(processed, homography, width, height), cts.Token);
                if (cts.IsCancellationRequested) return;
                warped = BaseImageEditService.ToBitmap(corrected);
            }

            _unwarpedBitmap = unwarped;
            _warpedBitmap = warped;
            ShowCurrentBitmap();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change: nothing to do
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Couldn't make the preview: {ex.Message}";
        }
    }

    // =================================================================================================================
    // Tools: Crop, Erase, Straighten, Skew
    // =================================================================================================================

    private static string ToolHintFor(Tool tool) => tool switch
    {
        Tool.Erase => "Drag on the page to paint it white.",
        Tool.Straighten => "Drag a line along something that should be level or upright — the page is turned to make it so.",
        Tool.Skew => "Photographed at an angle? Put the four corners on the page's corners (they may go past the photograph).",
        _ => "",
    };

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string name } && Enum.TryParse<Tool>(name, out var tool))
            SetTool(tool);
        SyncToolButtons();   // clicking the tool that is already on must not switch it off
    }

    private void SyncToolButtons()
    {
        CropToolButton.IsChecked = _tool == Tool.Crop;
        EraseToolButton.IsChecked = _tool == Tool.Erase;
        StraightenToolButton.IsChecked = _tool == Tool.Straighten;
        SkewToolButton.IsChecked = _tool == Tool.Skew;
    }

    /// <summary>Switches tool. Leaving the Skew tool without applying drops the corners that were being dragged; entering it starts from the correction already applied, or from a rectangle just
    /// inside the image.</summary>
    private void SetTool(Tool tool)
    {
        if (_tool == tool || _busy) { SyncToolButtons(); return; }
        _toolDragging = false;
        _skewDrag = -1;
        _tool = tool;
        _note = "";
        SyncToolButtons();

        EraseOptions.Visibility = tool == Tool.Erase ? Visibility.Visible : Visibility.Collapsed;
        SkewOptions.Visibility = tool == Tool.Skew ? Visibility.Visible : Visibility.Collapsed;
        ToolHint.Text = ToolHintFor(tool);
        RemoveSkewButton.IsEnabled = _skewQuad is not null;
        if (tool == Tool.Skew) _skewWork = _skewQuad ?? Quad.Inset(_raw.Width, _raw.Height, 0.06);

        RelayoutStage(resetCrop: false);
        Stage.Cursor = tool == Tool.Crop ? Cursors.Cross : tool == Tool.Skew ? Cursors.Arrow : Cursors.Cross;
    }

    private void BeginToolGesture(Point p)
    {
        _gestureStart = p;
        switch (_tool)
        {
            case Tool.Erase:
                _toolDragging = true;
                if (EraseBrushRadio.IsChecked == true)
                {
                    _brushPoints = new List<GeoPoint> { new(CanvasX(p.X), CanvasY(p.Y)) };
                    UpdateLiveBrush();
                }
                else
                {
                    EraseRubber.Visibility = Visibility.Visible;
                    Canvas.SetLeft(EraseRubber, p.X); Canvas.SetTop(EraseRubber, p.Y);
                    EraseRubber.Width = EraseRubber.Height = 0;
                }
                break;

            case Tool.Straighten:
                _toolDragging = true;
                StraightenLine.X1 = StraightenLine.X2 = p.X;
                StraightenLine.Y1 = StraightenLine.Y2 = p.Y;
                StraightenLine.Visibility = Visibility.Visible;
                break;

            case Tool.Skew:
                _skewDrag = HitQuadHandle(p);
                _toolDragging = _skewDrag >= 0;
                break;
        }
    }

    private void ContinueToolGesture(Point p)
    {
        switch (_tool)
        {
            case Tool.Erase when EraseBrushRadio.IsChecked == true:
            {
                var last = _brushPoints[^1];
                double dx = (CanvasX(p.X) - last.X) * _scale, dy = (CanvasY(p.Y) - last.Y) * _scale;
                if (dx * dx + dy * dy >= 4) { _brushPoints.Add(new GeoPoint(CanvasX(p.X), CanvasY(p.Y))); UpdateLiveBrush(); }   // a point every couple of screen units is smooth enough
                break;
            }
            case Tool.Erase:
                Canvas.SetLeft(EraseRubber, Math.Min(_gestureStart.X, p.X)); Canvas.SetTop(EraseRubber, Math.Min(_gestureStart.Y, p.Y));
                EraseRubber.Width = Math.Abs(p.X - _gestureStart.X); EraseRubber.Height = Math.Abs(p.Y - _gestureStart.Y);
                break;

            case Tool.Straighten:
                StraightenLine.X2 = p.X; StraightenLine.Y2 = p.Y;
                break;

            case Tool.Skew when _skewDrag >= 0:
            {
                // A corner goes where the pointer is. It may leave the image: out into the grey, up to as far as a crop may reach (BaseImageEditMath.MaxReach). What the corrected page
                // takes from beyond the photograph is white.
                int reach = BaseImageEditMath.MaxReach(_raw.Width, _raw.Height);
                var corner = new GeoPoint(Math.Clamp(CanvasX(p.X), -reach, _raw.Width + reach), Math.Clamp(CanvasY(p.Y), -reach, _raw.Height + reach));
                _skewWork = _skewDrag switch
                {
                    0 => _skewWork with { TopLeft = corner },
                    1 => _skewWork with { TopRight = corner },
                    2 => _skewWork with { BottomRight = corner },
                    _ => _skewWork with { BottomLeft = corner },
                };
                UpdateQuadVisuals();
                break;
            }
        }
    }

    private void EndToolGesture(Point p)
    {
        switch (_tool)
        {
            case Tool.Erase when EraseBrushRadio.IsChecked == true:
            {
                RemoveLiveBrush();
                if (_brushPoints.Count > 0) AddErasure(new EraseStroke(_brushPoints.ToList(), Math.Max(1, BrushSizeBox.Value / 2)));
                _brushPoints = new List<GeoPoint>();
                break;
            }
            case Tool.Erase:
            {
                EraseRubber.Visibility = Visibility.Collapsed;
                double left = Math.Min(_gestureStart.X, p.X), top = Math.Min(_gestureStart.Y, p.Y);
                double width = Math.Abs(p.X - _gestureStart.X), height = Math.Abs(p.Y - _gestureStart.Y);
                if (width >= 2 && height >= 2)   // a click without a drag is not a rectangle
                    AddErasure(new EraseRect(CanvasX(left), CanvasY(top), width / _scale, height / _scale));
                break;
            }
            case Tool.Straighten:
            {
                StraightenLine.Visibility = Visibility.Collapsed;
                double length = Math.Sqrt((p.X - _gestureStart.X) * (p.X - _gestureStart.X) + (p.Y - _gestureStart.Y) * (p.Y - _gestureStart.Y));
                if (length < 12) break;   // too short to say which way it points
                double turn = PerspectiveMath.StraightenAngle(
                    new GeoPoint(CanvasX(_gestureStart.X), CanvasY(_gestureStart.Y)), new GeoPoint(CanvasX(p.X), CanvasY(p.Y)));
                SetTool(Tool.Crop);   // like GIMP: the tool is used once, then you are back to cropping
                TurnTo(_angle + turn);
                _note = _note + $" Turned {turn:+0.0#;-0.0#} degrees to straighten.";
                UpdateCropVisuals();
                break;
            }
            case Tool.Skew:
                _skewDrag = -1;
                RelayoutStage(resetCrop: false);   // the stage grows (or shrinks back) to hold wherever the corners now are
                break;
        }
    }

    // ---- erased areas

    private void AddErasure(EraseShape shape)
    {
        _erasures = _erasures.Append(shape).ToList();
        RebuildEraseLayer();
        RecordHistory(coalesce: false);
    }

    private void ClearErasuresButton_Click(object sender, RoutedEventArgs e)
    {
        if (_erasures.Count == 0) return;
        _erasures = Array.Empty<EraseShape>();
        RebuildEraseLayer();
        RecordHistory(coalesce: false);
    }

    /// <summary>The white shape for an erased area, on the stage.</summary>
    private UIElement MakeEraseElement(EraseShape shape)
    {
        switch (shape)
        {
            case EraseRect r:
            {
                var rectangle = new Rectangle { Fill = Brushes.White, Width = Math.Max(1, r.Width * _scale), Height = Math.Max(1, r.Height * _scale), IsHitTestVisible = false };
                Canvas.SetLeft(rectangle, ScreenX(r.X)); Canvas.SetTop(rectangle, ScreenY(r.Y));
                return rectangle;
            }
            case EraseStroke stroke:
            {
                double thickness = Math.Max(1, stroke.Radius * 2 * _scale);
                if (stroke.Points.Count == 1)
                {
                    var dab = new Ellipse { Fill = Brushes.White, Width = thickness, Height = thickness, IsHitTestVisible = false };
                    Canvas.SetLeft(dab, ScreenX(stroke.Points[0].X) - thickness / 2); Canvas.SetTop(dab, ScreenY(stroke.Points[0].Y) - thickness / 2);
                    return dab;
                }
                var figure = new PathFigure { StartPoint = new Point(ScreenX(stroke.Points[0].X), ScreenY(stroke.Points[0].Y)) };
                figure.Segments.Add(new PolyLineSegment(stroke.Points.Skip(1).Select(pt => new Point(ScreenX(pt.X), ScreenY(pt.Y))), true) { IsSmoothJoin = true });
                return new System.Windows.Shapes.Path
                {
                    Data = new PathGeometry(new[] { figure }), Stroke = Brushes.White, StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
                };
            }
            default:
                return new Canvas();
        }
    }

    private void RebuildEraseLayer()
    {
        EraseLayer.Children.Clear();
        EraseLayer.Visibility = _tool == Tool.Skew ? Visibility.Collapsed : Visibility.Visible;   // they belong to the turned page, not the photograph
        foreach (var shape in _erasures)
            EraseLayer.Children.Add(MakeEraseElement(shape));
    }

    private void UpdateLiveBrush()
    {
        RemoveLiveBrush();
        _liveBrush = MakeEraseElement(new EraseStroke(_brushPoints.ToList(), Math.Max(1, BrushSizeBox.Value / 2)));
        EraseLayer.Children.Add(_liveBrush);
    }

    private void RemoveLiveBrush()
    {
        if (_liveBrush is not null) EraseLayer.Children.Remove(_liveBrush);
        _liveBrush = null;
    }

    // ---- skew correction

    private void UpdateQuadVisuals()
    {
        bool show = _tool == Tool.Skew;
        QuadShape.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var handle in _quadHandles) handle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var corners = _skewWork.Points.Select(c => new Point(ScreenX(c.X), ScreenY(c.Y))).ToArray();   // in the Skew tool the stage IS the photograph, so pixels map straight across
        QuadShape.Points = new PointCollection(corners);
        for (int i = 0; i < _quadHandles.Length; i++)
        {
            Canvas.SetLeft(_quadHandles[i], corners[i].X - _quadHandles[i].Width / 2);
            Canvas.SetTop(_quadHandles[i], corners[i].Y - _quadHandles[i].Height / 2);
        }
    }

    /// <summary>The smallest pixel rectangle holding all four corners (they may lie outside the image).</summary>
    private static PixelRect QuadBounds(Quad quad)
    {
        var corners = quad.Points;
        int left = (int)Math.Floor(corners.Min(c => c.X)), top = (int)Math.Floor(corners.Min(c => c.Y));
        int right = (int)Math.Ceiling(corners.Max(c => c.X)), bottom = (int)Math.Ceiling(corners.Max(c => c.Y));
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private int HitQuadHandle(Point stagePoint)
    {
        var corners = _skewWork.Points;
        int best = -1; double bestDistance = 16;
        for (int i = 0; i < corners.Length; i++)
        {
            double dx = ScreenX(corners[i].X) - stagePoint.X, dy = ScreenY(corners[i].Y) - stagePoint.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance) { best = i; bestDistance = distance; }
        }
        return best;
    }

    private void ResetSkewButton_Click(object sender, RoutedEventArgs e)
    {
        _skewWork = Quad.Inset(_raw.Width, _raw.Height, 0.06);
        UpdateQuadVisuals();
    }

    private void CancelSkewButton_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Crop);

    /// <summary>Makes the skew correction from the corners as they are now: refuses a shape that crosses itself, or a result bigger than a page may be; otherwise the page becomes the corrected
    /// rectangle, and everything that depended on the page as it was (the crop, the erased areas) starts over.</summary>
    private void ApplySkewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PerspectiveMath.IsConvex(_skewWork))
        {
            InfoText.Text = "The four corners must make a proper four-sided shape — they cross over or fold back on themselves. Drag them so they go round the page in order.";
            return;
        }
        var stage = PerspectiveStage.From(_skewWork);
        if ((long)stage.Width * stage.Height > _maxPixels)
        {
            InfoText.Text = $"That would make a page of {stage.Width:N0} × {stage.Height:N0} pixels — over the {_maxPixels / 1_000_000} megapixel limit.";
            return;
        }
        SetSkew(_skewWork);
    }

    private void RemoveSkewButton_Click(object sender, RoutedEventArgs e) => SetSkew(null);

    private void SetSkew(Quad? quad)
    {
        _skewQuad = quad;
        _skew = quad is { } q ? PerspectiveStage.From(q) : null;
        (_baseWidth, _baseHeight) = _skew is null ? (_raw.Width, _raw.Height) : (_skew.Width, _skew.Height);
        _crop = default;                  // an empty crop: the layout starts over with the whole corrected page
        SetTool(Tool.Crop);               // (this also puts away the one-off message, so the note below is set AFTER it)
        ClearErasuresBecauseThePageChanged(quad is null ? "restored to the photograph" : "skew-corrected");
        RebuildEraseLayer();
        UpdateCropVisuals();
        _ = RefreshPreviewAsync();
        RecordHistory(coalesce: false);
    }

    // =================================================================================================================
    // Undo / redo
    // =================================================================================================================

    private EditState CurrentState() => new(_angle, _crop, _settings.Clone(), _skewQuad, _erasures);

    private static bool SameState(EditState a, EditState b) =>
        Math.Abs(a.Angle - b.Angle) < 1e-9 && a.Crop == b.Crop && a.Effects.HasSameValues(b.Effects) && a.Skew == b.Skew && a.Erasures.SequenceEqual(b.Erasures);

    /// <summary>Adds the current state to the history (nothing if it is the same as the last). Changes that arrive in a stream — a slider being dragged, a box being spun —
    /// are folded into one step with <paramref name="coalesce"/>, so Undo goes back over the whole movement, not one tick of it.</summary>
    private void RecordHistory(bool coalesce)
    {
        if (_applying || !_ready || _history.Count == 0) return;
        var state = CurrentState();
        if (SameState(_history[_historyIndex], state)) { UpdateButtons(); return; }

        if (_historyIndex < _history.Count - 1)                       // a new change ends the redo branch
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        var now = DateTime.UtcNow;
        if (coalesce && _lastRecordCoalescible && _historyIndex > 0 && now - _lastRecordUtc < CoalesceWindow)
        {
            _history[_historyIndex] = state;                          // still the same gesture
        }
        else
        {
            _history.Add(state);
            if (_history.Count > MaxHistory) _history.RemoveAt(0); else _historyIndex++;
        }
        _lastRecordUtc = now;
        _lastRecordCoalescible = coalesce;
        UpdateButtons();
    }

    private void ApplyState(EditState state)
    {
        _applying = true;
        try
        {
            _angle = state.Angle;
            _updatingFields = true;
            AngleBox.Value = _angle;
            _updatingFields = false;
            _settings.CopyFrom(state.Effects);
            _skewQuad = state.Skew;
            _skew = state.Skew is { } q ? PerspectiveStage.From(q) : null;
            (_baseWidth, _baseHeight) = _skew is null ? (_raw.Width, _raw.Height) : (_skew.Width, _skew.Height);
            _erasures = state.Erasures;
            _crop = state.Crop;
            _tool = Tool.Crop;               // a state is always a page as it will be saved: back to the Crop tool, with its options put away
            _note = "";
            EraseOptions.Visibility = SkewOptions.Visibility = Visibility.Collapsed;
            ToolHint.Text = ToolHintFor(Tool.Crop);
            SyncToolButtons();
            RelayoutStage(resetCrop: false);
        }
        finally
        {
            _applying = false;
        }
        _effectsTimer.Stop(); _effectsTimer.Start();
        _lastRecordCoalescible = false;
        UpdateButtons();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        _historyTimer.Stop();
        RecordHistory(coalesce: false);                 // make sure what is on screen now is in the history before stepping back from it
        if (_historyIndex <= 0) return;
        ApplyState(_history[--_historyIndex]);
    }

    private void Redo()
    {
        if (_historyIndex >= _history.Count - 1) return;
        ApplyState(_history[++_historyIndex]);
    }

    private void UpdateButtons()
    {
        UndoButton.IsEnabled = _historyIndex > 0 && !_busy;
        RedoButton.IsEnabled = _historyIndex < _history.Count - 1 && !_busy;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        // A number box with typing of its own to undo keeps Ctrl+Z; otherwise it belongs to the editor.
        if (Keyboard.FocusedElement is TextBox { CanUndo: true } or TextBox { CanRedo: true }) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Z && !shift) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { Redo(); e.Handled = true; }
    }

    // =================================================================================================================
    // Save / cancel
    // =================================================================================================================

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        SaveButton.IsEnabled = CancelButton.IsEnabled = !busy;
        EffectsHost.IsEnabled = !busy;
        BusyText.Text = message;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        UpdateButtons();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_tool == Tool.Skew)
        {
            InfoText.Text = "Press Apply skew correction (or Cancel) first — the corners on screen haven't been used yet.";
            return;
        }
        _historyTimer.Stop();
        SetBusy(true, "Saving…");
        try
        {
            var effects = _settings.Clone();
            effects.AutoCropEnabled = false;
            var page = await BaseImageEditService.RenderAsync(_raw, _angle, _crop, effects, _context.Processing, _skew, _erasures);
            Result = new BaseImageEditResult(page, _angle, _crop, effects.ChangesPixels, _skew, _erasures.Count > 0);
            SetBusy(false);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, $"Could not prepare the edited page: {ex.Message}", "Edit Base Image", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (Result is not null) return;                                   // Save succeeded
        // Only worth asking if what is on screen differs from how it started (after undoing everything it doesn't, whatever is left to redo).
        if (_history.Count > 0 && !SameState(_history[0], CurrentState()))
        {
            var answer = MessageBox.Show(this, "Discard the changes you have made to this image?", "Edit Base Image", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
    }
}
