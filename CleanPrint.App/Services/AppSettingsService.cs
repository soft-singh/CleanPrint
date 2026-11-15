using System.IO;
using System.Text.Json;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>Every control whose value should carry over between app launches — split/print
/// settings and preferences, not per-job or per-page state (page content, polygons, zoom, and
/// window placement are all persisted separately already).</summary>
public sealed class AppSettings
{
    public int SplitRows { get; set; } = 1;
    public int SplitColumns { get; set; } = 1;
    public int OverlapPixels { get; set; }

    public string? SelectedPrinter { get; set; }

    /// <summary>Matched by name against whatever paper sizes the saved printer reports being
    /// available at load time — a name from a different, no-longer-selected printer just won't
    /// match anything, which is fine.</summary>
    public string? SelectedPaperSizeName { get; set; }

    public bool IsLandscape { get; set; }
    public double ScalePercent { get; set; } = 100;
    public bool FitToPage { get; set; } = true;

    public double MarginLeftInches { get; set; } = 0.25;
    public double MarginTopInches { get; set; } = 0.25;
    public double MarginRightInches { get; set; } = 0.25;
    public double MarginBottomInches { get; set; } = 0.25;

    public bool ShowPageNumbers { get; set; }
    public string SelectedPageNumberPosition { get; set; } = "Footer Center";

    // Deliberately no Copies here — copies is a per-print-job decision (e.g. "print 3 of this
    // specific batch"), not a preference that should persist or carry over silently to the next
    // print. MainViewModel.Copies always starts at 1 for the same reason.
    public bool PrintWholePage { get; set; }

    /// <summary>The preset (by name, matching a file in EffectPresetService's fixed folder) to
    /// apply automatically whenever a NEW job is loaded (see MainViewModel.LoadJob) — General
    /// settings tab. Null/empty means no preset is configured, in which case NO initial effect is
    /// applied at all (ImageEffectSettings.NoEffects()) — a freshly-opened job starts predictably
    /// rather than silently carrying over whatever effect settings a previous job happened to leave
    /// active, which was the original complaint that led to this whole feature (background
    /// whitening being always-on and unavoidable).</summary>
    public string? DefaultPresetName { get; set; }

    /// <summary>Ctrl+scroll zoom multiplier per wheel tick — see
    /// MainViewModel.ZoomWheelSensitivity's own doc comment.</summary>
    public double ZoomWheelSensitivity { get; set; } = 1.1;

    /// <summary>Effects reprocess debounce delay, in milliseconds — see
    /// MainViewModel.EffectsDebounceMilliseconds's own doc comment.</summary>
    public int EffectsDebounceMilliseconds { get; set; } = 300;

    /// <summary>Whether exported images get a sidecar job-link file — see
    /// MainViewModel.LinkExportsToJobs's own doc comment.</summary>
    public bool LinkExportsToJobs { get; set; }

    /// <summary>Manual toolbar scale multiplier — see MainViewModel.UiScale's own doc comment.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Scale multiplier for dialogs and panels (Effects, Print, Settings, ...), separate from the
    /// toolbar scale — see MainViewModel.DialogScale's own doc comment. Files saved before this existed have
    /// no value and get 1.0, which is also what dialogs looked like before the scale reached them at all.</summary>
    public double DialogScale { get; set; } = 1.0;

    /// <summary>How much darker (negative) or lighter (positive) than the theme's own the toolbar icons are, -100 to +100 — see MainViewModel.IconLightness. (Settings files written before this
    /// have an old "IconColor" instead, which is simply ignored: the icons now follow the theme.)</summary>
    public double IconLightness { get; set; }

    /// <summary>The name of the chosen theme (see Core's ThemeCatalog). Empty in a settings file written before themes existed: then <see cref="DarkMode"/> decides (Dark, or Classic).</summary>
    public string Theme { get; set; } = "";

    /// <summary>The old light/dark switch. Still written — true when the chosen theme is a dark one — so an older version of the program reading the same file still gets the right idea; read only
    /// when <see cref="Theme"/> is empty.</summary>
    public bool DarkMode { get; set; }

    /// <summary>Job cache limits (managed from the Open Job dialog) — each can be switched on/off
    /// independently, and both default to OFF so nothing nags until the person opts in. Reaching a
    /// limit only shows a warning in the main window; nothing is ever deleted automatically. Edited in
    /// Settings > General; see MainViewModel's JobCache* properties.</summary>
    public bool JobCacheLimitCountEnabled { get; set; }
    public int JobCacheMaxJobs { get; set; } = 100;
    public bool JobCacheLimitSizeEnabled { get; set; }
    public int JobCacheMaxSizeMb { get; set; } = 2048;

    /// <summary>Whether reaching an enabled limit shows the warning in the main window's status bar.
    /// On by default; turning it off silences the message but doesn't change the limits themselves.</summary>
    public bool JobCacheShowWarning { get; set; } = true;

    /// <summary>Default polygon style for the NEXT new polygon drawn — see
    /// MainViewModel's _defaultPolygonColor/_defaultPolygonThickness/_defaultHasStroke/
    /// _defaultFillColor/_defaultFillOpacity fields. Colors stored as "#RRGGBB" hex, matching how
    /// PolygonAnnotation itself stores them.</summary>
    public string DefaultStrokeColorHex { get; set; } = "#0000FF"; // Colors.Blue
    public double DefaultStrokeThickness { get; set; } = 3.0;
    public bool DefaultHasStroke { get; set; } = true;
    public string? DefaultFillColorHex { get; set; }
    public double DefaultFillOpacity { get; set; } = 50.0;
}

/// <summary>Loads/saves the split &amp; print controls' last-used values, the same way
/// WindowPlacementService does for the window itself, so the app reopens configured the way it
/// was left rather than back at hardcoded defaults every time.</summary>
public static class AppSettingsService
{
    private static string FilePath => Path.Combine(CleanPrintPaths.LocalAppDataRoot, "app-settings.json");

    /// <summary>Returns null if nothing has been saved yet (first run) or the file is unreadable.</summary>
    public static AppSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null; // corrupt/unreadable file — fall back to first-run behavior rather than crash
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best effort — losing the saved settings isn't worth crashing over
        }
    }
}
