using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>Decodes a page_NNN.png/.jpg file (written by the Watcher, or by Open Image(s)) into a
/// BGRA32 CapturedPage. Despite the name, this already works for any format WPF's BitmapDecoder can
/// read (it detects format from file content, not extension) — kept as "PngPageLoader" since PNG
/// remains the default/primary format.</summary>
public static class PngPageLoader
{
    /// <param name="overrideDpi">If provided (from JobCaptureInfoService — see its own doc comment
    /// for why this exists), used INSTEAD of the file's own embedded DPI metadata. OpenCvSharp-
    /// written files (the Watcher's default save path, and Export) carry no DPI metadata at all, so
    /// relying on frame.DpiX there would silently read back as 96 regardless of the true capture
    /// DPI. Jobs without this override (e.g. from Open Image(s), which still uses WPF's own encoder
    /// and correctly embeds DPI) fall back to the file's own metadata as before.</param>
    public static CapturedPage Load(string pngPath, int pageNumber, string jobId, int? overrideDpi = null)
    {
        using var stream = File.OpenRead(pngPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        int dpi = overrideDpi ?? (frame.DpiX > 0 ? (int)Math.Round(frame.DpiX) : 96);

        return new CapturedPage
        {
            PageNumber = pageNumber,
            Width = width,
            Height = height,
            Dpi = dpi,
            PixelData = buffer,
            SourceJobId = jobId,
        };
    }
}
