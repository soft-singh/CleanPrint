using System.IO;
using System.Text.Json;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;

namespace CleanPrint.Core.Services;

/// <summary>Loads/saves ExportQualitySettings to a fixed location under CleanPrintPaths — lives in
/// Core (not App) specifically so CleanPrint.Watcher can read the same file without needing to
/// reference the App project.</summary>
public static class ExportQualitySettingsService
{
    private static string FilePath => Path.Combine(CleanPrintPaths.LocalAppDataRoot, "export-quality.json");

    /// <summary>Returns null if nothing has been saved yet (first run) or the file is unreadable —
    /// the caller falls back to ExportQualitySettings' own hardcoded defaults in that case.</summary>
    public static ExportQualitySettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<ExportQualitySettings>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(ExportQualitySettings settings)
    {
        try
        {
            Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best effort — losing this isn't worth crashing over
        }
    }
}
