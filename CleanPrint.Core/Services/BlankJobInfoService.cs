using System.IO;
using System.Text.Json;
using CleanPrint.Core.Models;

namespace CleanPrint.Core.Services;

/// <summary>Loads/saves blank-job.json inside a job's own folder — see BlankJobInfo. Null from Load means "not a blank job" (or unreadable,
/// which is treated the same way: the page controls simply stay off rather than guessing at a page size).</summary>
public static class BlankJobInfoService
{
    private const string FileName = "blank-job.json";

    public static BlankJobInfo? Load(string jobFolder)
    {
        try
        {
            string path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            var info = JsonSerializer.Deserialize<BlankJobInfo>(File.ReadAllText(path));
            // A file with a nonsensical size is as good as none: new pages couldn't be made from it.
            return info is { WidthPx: > 0, HeightPx: > 0, Dpi: > 0 } ? info : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string jobFolder, BlankJobInfo info)
    {
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, FileName), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }
}
