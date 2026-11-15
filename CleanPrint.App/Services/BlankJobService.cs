using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;
using CleanPrint.Core.Services;

namespace CleanPrint.App.Services;

/// <summary>
/// Creates the files of a BLANK job: a folder under Jobs holding white page_NNNN.png files, exactly as Open Image(s) or a print capture would leave one, plus the
/// small sidecars that tell the rest of the app what it is (see BlankJobInfo, JobCaptureInfo, JobViewState). After this, ViewModel.LoadJob opens it like any other job.
/// </summary>
public static class BlankJobService
{
    /// <summary>Makes the job folder and returns its path. Throws on failure, having first removed anything it half-made.</summary>
    public static string CreateJob(string paperName, bool landscape, int widthPx, int heightPx, int dpi, int pageCount, bool isPrivate = false)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));

        string baseId = $"Blank-{SanitizeForFolderName(paperName)}-{DateTime.Now:yyyyMMdd-HHmmss}";
        // In the job cache, or — in private mode — in the private area with the private prefix (and a number added if two are made in the same second).
        string jobFolder = CleanPrintPaths.NewJobFolder(baseId, isPrivate);
        try
        {
            var white = NewWhitePixels(widthPx, heightPx);       // one buffer, written once per page
            for (int page = 1; page <= pageCount; page++)
                WriteWhitePage(Path.Combine(jobFolder, PageRenumbering.FileName(page)), widthPx, heightPx, dpi, white);

            // The DPI the page loader should use (it ignores nothing, but a PNG's own DPI tag is the weaker source: see JobCaptureInfo).
            JobCaptureInfoService.Save(jobFolder, dpi);
            BlankJobInfoService.Save(jobFolder, new BlankJobInfo { PaperName = paperName, Landscape = landscape, WidthPx = widthPx, HeightPx = heightPx, Dpi = dpi });

            // A blank page has no background to whiten and nothing to crop to: with the default effects, auto-crop would see an empty page and
            // could shrink it. So the job starts with every effect off, whatever the default preset says; the Effects panel is still there to turn any on.
            JobViewStateService.Save(jobFolder, new JobViewState { EffectSettings = ImageEffectSettings.NoEffects(), Zoom = 1.0 });
            return jobFolder;
        }
        catch
        {
            try { Directory.Delete(jobFolder, recursive: true); } catch { /* best effort: a stray empty folder is harmless */ }
            throw;
        }
    }

    /// <summary>Adds one white page to an existing blank job, at the job's own page size.</summary>
    public static void WriteBlankPage(string path, BlankJobInfo info) =>
        WriteWhitePage(path, info.WidthPx, info.HeightPx, info.Dpi, NewWhitePixels(info.WidthPx, info.HeightPx));

    private static byte[] NewWhitePixels(int widthPx, int heightPx)
    {
        var pixels = new byte[(long)widthPx * heightPx];        // 8-bit grey: 1 byte a pixel, and white compresses to almost nothing
        Array.Fill(pixels, (byte)255);
        return pixels;
    }

    private static void WriteWhitePage(string path, int widthPx, int heightPx, int dpi, byte[] whitePixels)
    {
        // WPF's own PNG encoder, so the file also carries its DPI. Written to a temp name and moved into place, so a page file is never half-written.
        var bitmap = BitmapSource.Create(widthPx, heightPx, dpi, dpi, PixelFormats.Gray8, null, whitePixels, widthPx);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
            encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }

    private static string SanitizeForFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length == 0 ? "Page" : name.Length > 30 ? name[..30] : name;
    }
}
