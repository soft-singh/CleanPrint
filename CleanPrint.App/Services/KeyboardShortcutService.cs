using System.IO;
using System.Text.Json;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>Loads/saves customizable keyboard shortcuts to a fixed location, separate from
/// AppSettings — same simple JSON-file pattern as AppSettingsService/EffectPresetService.</summary>
public static class KeyboardShortcutService
{
    /// <summary>Bump when a default changes in a way existing saved files should pick up (and add a
    /// matching step to Migrate).</summary>
    private const int CurrentSchemaVersion = 1;

    private static string FilePath => Path.Combine(CleanPrintPaths.LocalAppDataRoot, "keyboard-shortcuts.json");

    /// <summary>Returns null if nothing has been saved yet (first run) or the file is unreadable —
    /// the caller falls back to KeyboardShortcuts' own hardcoded defaults in that case.</summary>
    public static KeyboardShortcuts? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var loaded = JsonSerializer.Deserialize<KeyboardShortcuts>(File.ReadAllText(FilePath));
            if (loaded is not null) Migrate(loaded);
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One-time upgrades for files saved under an older set of defaults. Safe to run on every
    /// load: each step only fires for a file older than the version that introduced it, and Save stamps
    /// the current version, so a value the person picks afterwards is never overridden again.</summary>
    private static void Migrate(KeyboardShortcuts loaded)
    {
        // v0 -> v1: Finish Polygon (Leave Open) moved from End to the backtick key. An "End" in a v0 file
        // is the old DEFAULT (that's all this shortcut ever shipped with), not a deliberate choice.
        if (loaded.SchemaVersion < 1 && loaded.FinishPolygonOpen == "End")
            loaded.FinishPolygonOpen = new KeyboardShortcuts().FinishPolygonOpen;
    }

    public static void Save(KeyboardShortcuts shortcuts)
    {
        try
        {
            shortcuts.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
            var json = JsonSerializer.Serialize(shortcuts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best effort — losing this isn't worth crashing over
        }
    }
}
