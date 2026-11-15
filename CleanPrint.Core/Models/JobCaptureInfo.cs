namespace CleanPrint.Core.Models;

/// <summary>
/// Records the DPI a job was actually captured at — stored as capture-info.json inside the job's
/// own folder, written by the Watcher right after rendering. Exists because OpenCvSharp's
/// Cv2.ImWrite (used for PNG/JPG compression control — see ExportQualitySettings) does not write
/// DPI/resolution metadata into the file at all, unlike WPF's own encoders, which is what
/// PngPageLoader used to read a page's DPI from. Without this sidecar, every page's DPI would
/// silently read back as 96 (the standard "no metadata" fallback) regardless of the actual capture
/// DPI, which would then be wrong for every physical-size-dependent calculation in the app: print/
/// export scale, split-tile sizing, polygon stroke-thickness scaling, and background-whitening
/// kernel sizing.
///
/// Only written by jobs captured via the Watcher — jobs created via "Open Image(s)" still use WPF's
/// PngBitmapEncoder for that initial save, which correctly embeds DPI on its own, so no sidecar is
/// needed there; its absence is exactly the signal to fall back to the file's own embedded DPI.
/// </summary>
public sealed class JobCaptureInfo
{
    public int Dpi { get; set; }
}
