using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CleanPrint.App.Services;

/// <summary>
/// The app-wide "Toolbar scale" (Settings > General), as something any window can bind to without
/// needing a reference to MainViewModel — the Effects panel's DataContext is its ImageEffectSettings,
/// for instance, and floating windows have no view model at all. MainViewModel.UiScale is the real,
/// persisted setting; it pushes every change into this singleton.
/// </summary>
public sealed class UiScaleSource : INotifyPropertyChanged
{
    public static UiScaleSource Instance { get; } = new();

    private double _scale = 1.0;
    public double Scale
    {
        get => _scale;
        set
        {
            if (Math.Abs(_scale - value) < 1e-9) return;
            _scale = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>How a window or panel follows the app-wide UI scale (see <see cref="UiScaling"/>).</summary>
public enum UiScaleMode
{
    /// <summary>Not scaled.</summary>
    None,

    /// <summary>Scaled by whatever the scale is when it opens, and left alone after that. For dialogs
    /// that are opened fresh each time. Notably the Settings dialog: it contains the very control that
    /// changes the scale, and a window that resizes itself under the mouse while the slider is being
    /// dragged is unusable.</summary>
    Snapshot,

    /// <summary>Follows every change as it happens — for persistent windows/panels (the Print dialog is
    /// hidden and re-shown, never recreated; the docked Effects panel is always alive).</summary>
    Live,

    /// <summary>Window only: resizes with the scale but does NOT transform its content. For a window
    /// whose content scales itself (the floating Effects window hosts the Effects panel, which already
    /// has Live) — transforming the window's content too would scale it twice.</summary>
    SizeOnly,
}

/// <summary>
/// Makes a window or panel follow the "Toolbar scale" setting: <c>svc:UiScaling.Mode="Live"</c> on the
/// root element is all a XAML file needs. The content gets a LayoutTransform (LayoutTransform, not
/// RenderTransform, so the layout actually grows/shrinks rather than just being drawn bigger and
/// clipped), and a Window is also resized by the same ratio — authored sizes assume 100%, so without
/// that a 125% scale would just clip the bigger content.
///
/// Applied at initialization rather than on Loaded, so the first frame is already the right size
/// instead of visibly jumping. Deliberately never applied to the preview area of the main window (see
/// MainWindow.xaml — the Freeze Row overlay's coordinate math lives there).
/// </summary>
public static class UiScaling
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(UiScaleMode), typeof(UiScaling), new PropertyMetadata(UiScaleMode.None, OnModeChanged));

    public static UiScaleMode GetMode(DependencyObject d) => (UiScaleMode)d.GetValue(ModeProperty);
    public static void SetMode(DependencyObject d, UiScaleMode value) => d.SetValue(ModeProperty, value);

    // Private bookkeeping, kept as attached properties so no per-window dictionary (and so no leak) is needed.
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(UiScaling), new PropertyMetadata(false));

    /// <summary>The scale a window's size currently reflects (1.0 = as authored in XAML).</summary>
    private static readonly DependencyProperty AppliedScaleProperty = DependencyProperty.RegisterAttached(
        "AppliedScale", typeof(double), typeof(UiScaling), new PropertyMetadata(1.0));

    /// <summary>Bound to UiScaleSource.Scale; its change callback is what resizes a live window. (A
    /// binding rather than an event subscription so the singleton can't keep closed windows alive.)</summary>
    private static readonly DependencyProperty TrackedScaleProperty = DependencyProperty.RegisterAttached(
        "TrackedScale", typeof(double), typeof(UiScaling), new PropertyMetadata(1.0, OnTrackedScaleChanged));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || GetMode(element) == UiScaleMode.None) return;

        // This runs while XAML is still being parsed (before the window's content exists) for XAML
        // usage, or after construction for code usage — Initialized covers the first, IsInitialized the second.
        if (element.IsInitialized)
            Attach(element);
        else
            element.Initialized += AttachWhenInitialized;
    }

    private static void AttachWhenInitialized(object? sender, EventArgs e)
    {
        var element = (FrameworkElement)sender!;
        element.Initialized -= AttachWhenInitialized;
        Attach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        if ((bool)element.GetValue(AttachedProperty)) return;
        element.SetValue(AttachedProperty, true);

        var mode = GetMode(element);
        var source = UiScaleSource.Instance;

        if (mode is UiScaleMode.Snapshot or UiScaleMode.Live)
        {
            // A Window can't carry a layout transform itself, so its content does.
            FrameworkElement? target = element is Window window ? window.Content as FrameworkElement : element;
            if (target is not null)
            {
                var transform = new ScaleTransform(source.Scale, source.Scale);
                if (mode == UiScaleMode.Live)
                {
                    var binding = new Binding(nameof(UiScaleSource.Scale)) { Source = source, Mode = BindingMode.OneWay };
                    BindingOperations.SetBinding(transform, ScaleTransform.ScaleXProperty, binding);
                    BindingOperations.SetBinding(transform, ScaleTransform.ScaleYProperty, binding);
                }
                target.LayoutTransform = transform;
            }
        }

        if (element is Window w)
        {
            if (mode is UiScaleMode.Live or UiScaleMode.SizeOnly)
            {
                // Delivering the current value triggers the first resize through the same callback that
                // handles every later change.
                BindingOperations.SetBinding(w, TrackedScaleProperty,
                    new Binding(nameof(UiScaleSource.Scale)) { Source = source, Mode = BindingMode.OneWay });
            }
            else
            {
                ResizeWindow(w, source.Scale);
                w.SetValue(AppliedScaleProperty, source.Scale);
            }
        }
    }

    private static void OnTrackedScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w) return;

        double applied = (double)w.GetValue(AppliedScaleProperty);
        double now = (double)e.NewValue;
        if (applied <= 0 || now <= 0) return;

        ResizeWindow(w, now / applied);
        w.SetValue(AppliedScaleProperty, now);
    }

    /// <summary>Multiplies a window's size (and minimum size) by the ratio, within the screen's work area.
    /// A dimension the window sizes to its own content (SizeToContent) is left to do that; a maximized or
    /// minimized window is left alone (its size isn't ours to set).</summary>
    private static void ResizeWindow(Window w, double ratio)
    {
        if (Math.Abs(ratio - 1.0) < 0.001) return;
        if (w.WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        bool autoWidth = w.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        bool autoHeight = w.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

        if (!autoWidth)
        {
            w.MinWidth = Math.Min(w.MinWidth * ratio, work.Width);
            double current = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
            if (!double.IsNaN(current)) w.Width = Math.Min(current * ratio, work.Width);
        }
        if (!autoHeight)
        {
            w.MinHeight = Math.Min(w.MinHeight * ratio, work.Height);
            double current = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
            if (!double.IsNaN(current)) w.Height = Math.Min(current * ratio, work.Height);
        }

        // A window that's already on screen and just grew may now hang off an edge — pull it back in.
        if (w.IsLoaded)
        {
            w.Left = Math.Max(work.Left, Math.Min(w.Left, work.Right - w.Width));
            w.Top = Math.Max(work.Top, Math.Min(w.Top, work.Bottom - w.Height));
        }
    }
}
