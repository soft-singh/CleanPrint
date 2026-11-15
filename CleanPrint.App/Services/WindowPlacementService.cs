using System.IO;
using System.Text.Json;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

public sealed class WindowPlacementSettings
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 900;
    public bool IsMaximized { get; set; }
}

/// <summary>Loads/saves the window's last position, size, and maximized state so the app reopens
/// the way the person left it, instead of at a fixed pixel size that may not fit every screen.</summary>
public static class WindowPlacementService
{
    private static string FilePath => Path.Combine(CleanPrintPaths.LocalAppDataRoot, "window-placement.json");

    /// <summary>Returns null if nothing has been saved yet (first run) or the file is unreadable.</summary>
    public static WindowPlacementSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WindowPlacementSettings>(json);
        }
        catch
        {
            return null; // corrupt/unreadable file — fall back to first-run behavior rather than crash
        }
    }

    public static void Save(WindowPlacementSettings settings)
    {
        try
        {
            Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best effort — losing the saved placement isn't worth crashing over
        }
    }
}
