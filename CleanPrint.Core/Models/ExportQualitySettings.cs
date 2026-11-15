namespace CleanPrint.Core.Models;

/// <summary>
/// Compression/quality settings for saving images to disk — shared between CleanPrint.App's own
/// Export feature and CleanPrint.Watcher's XPS-to-PNG capture step, via the same persisted file
/// (see ExportQualitySettingsService), so one setting controls disk usage everywhere images get
/// written rather than needing to be configured twice.
/// </summary>
public sealed class ExportQualitySettings
{
    /// <summary>PNG is always lossless regardless of this — it only trades encode time for file
    /// size, never visual fidelity. 0 = no compression (fastest, largest file), 9 = maximum
    /// compression (slowest, smallest file). Applies both to Export's PNG output and to every
    /// page the Watcher captures when UseJpegForCapture is off.</summary>
    public int PngCompressionLevel { get; set; } = 6;

    /// <summary>JPG quality, 1-100 — lossy, so higher values mean both better visual fidelity and
    /// larger files. Applies to Export's JPG output, and to the Watcher's own capture when
    /// UseJpegForCapture is on.</summary>
    public int JpgQuality { get; set; } = 90;

    /// <summary>DPI the Watcher renders each captured page at. This is by far the single biggest
    /// lever on captured file size — going from 600 to 300 DPI, for example, cuts pixel count (and
    /// therefore raw data size before any compression) by 4x, since it applies to both dimensions.
    /// Most virtual printers default to 150-300; 600 (this app's original default) is unusually
    /// high for on-screen review and even for most printing, and is really only worth it for
    /// OCR-heavy workflows or reproducing very fine print. Re-read fresh per job by the Watcher
    /// (not cached at startup), so a change here applies to the very next captured job without
    /// needing to restart the Watcher.</summary>
    public int CaptureDpi { get; set; } = 300;

    /// <summary>When on, the Watcher saves each captured page as JPG (lossy, using JpgQuality)
    /// instead of PNG (lossless, using PngCompressionLevel) — a much bigger size reduction than
    /// tuning PNG compression alone, at the cost of some (usually minor, especially above ~85
    /// quality) generational quality loss baked in from the very first save, since every later
    /// effect/export starts from this already-lossy source. Off by default — PNG remains the safer,
    /// fully-lossless choice unless disk usage is a real constraint. Applies only to newly-captured
    /// jobs; existing page_*.png files are never converted retroactively.</summary>
    public bool UseJpegForCapture { get; set; }

    /// <summary>The format the Export dialogs offer first: "png" or "jpg" (the two formats Export writes). The Save box still lets you choose the other one each time.</summary>
    public string DefaultExportFormat { get; set; } = "png";
}
