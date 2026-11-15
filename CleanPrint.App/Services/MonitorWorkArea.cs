using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CleanPrint.App.Services;

/// <summary>
/// The usable area (the screen minus the taskbar) of the monitor a window is on, in WPF's device-independent units. SystemParameters.WorkArea only ever describes the PRIMARY monitor,
/// which is wrong for a window opening on a second one — a laptop's small screen beside a big monitor, say. Falls back to that if the monitor can't be found.
/// </summary>
public static class MonitorWorkArea
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static Rect For(Window? window)
    {
        try
        {
            if (window is not null)
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    {
                        // The monitor reports device pixels; WPF positions windows in device-independent units.
                        Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
                        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
                        return new Rect(topLeft, bottomRight);
                    }
                }
            }
        }
        catch
        {
            // fall through to the primary monitor
        }
        return SystemParameters.WorkArea;
    }
}
