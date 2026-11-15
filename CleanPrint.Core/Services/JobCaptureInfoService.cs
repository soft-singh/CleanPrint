using System.IO;
using System.Text.Json;
using CleanPrint.Core.Models;

namespace CleanPrint.Core.Services;

/// <summary>Loads/saves capture-info.json inside a job's own folder — see JobCaptureInfo's own doc
/// comment for what this is and why it exists. Lives in Core (not App) so CleanPrint.Watcher can
/// write it without depending on the App project.</summary>
public static class JobCaptureInfoService
{
    private const string FileName = "capture-info.json";

    /// <summary>Null if this job has no sidecar (e.g. created via "Open Image(s)", which doesn't
    /// need one — see JobCaptureInfo's doc comment) or it's unreadable. The caller falls back to
    /// the image file's own embedded DPI metadata in that case.</summary>
    public static JobCaptureInfo? Load(string jobFolder)
    {
        try
        {
            string path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<JobCaptureInfo>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string jobFolder, int dpi)
    {
        try
        {
            Directory.CreateDirectory(jobFolder);
            var info = new JobCaptureInfo { Dpi = dpi };
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(jobFolder, FileName), json);
        }
        catch
        {
            // best effort — losing this isn't worth failing the capture over; worst case the
            // fallback-to-embedded-DPI path in PngPageLoader applies (and is wrong, as before this
            // fix existed, but no WORSE than before)
        }
    }
}
