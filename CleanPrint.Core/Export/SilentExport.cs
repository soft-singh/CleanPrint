using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CleanPrint.Core.Paths;

namespace CleanPrint.Core.Export;

/// <summary>An image format the user can pick: a short id (what is stored in the settings file), a label for the list, and the file extension.</summary>
public sealed record ImageFormatOption(string Id, string Label, string Extension);

/// <summary>The image formats on offer. Export (from the main window and the Print dialog) writes PNG and JPEG; the silent export can also write BMP and TIFF.</summary>
public static class ImageFormats
{
    public static IReadOnlyList<ImageFormatOption> Manual { get; } = new[]
    {
        new ImageFormatOption("png", "PNG (lossless)", ".png"),
        new ImageFormatOption("jpg", "JPEG (smaller, lossy)", ".jpg"),
    };

    public static IReadOnlyList<ImageFormatOption> Silent { get; } = new[]
    {
        new ImageFormatOption("png", "PNG (lossless)", ".png"),
        new ImageFormatOption("jpg", "JPEG (smaller, lossy)", ".jpg"),
        new ImageFormatOption("bmp", "BMP (uncompressed bitmap)", ".bmp"),
        new ImageFormatOption("tif", "TIFF", ".tif"),
    };

    /// <summary>The option with this id (any case; "jpeg" and "tiff" are accepted as well), or the first of the list if there isn't one.</summary>
    public static ImageFormatOption Find(IReadOnlyList<ImageFormatOption> list, string? id)
    {
        string wanted = (id ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (wanted == "jpeg") wanted = "jpg";
        if (wanted == "tiff") wanted = "tif";
        return list.FirstOrDefault(o => o.Id == wanted) ?? list[0];
    }
}

/// <summary>
/// SILENT EXPORT: when on, a print job sent to the Clean Print printer is saved straight to a folder as image files — in the chosen format, named from the chosen pattern — and the
/// CleanPrint window is never opened. The Watcher (which does the capturing, and has no window) does it, reading these settings fresh for every job from a file the Settings dialog writes
/// (see <see cref="SilentExportSettingsService"/>), so changing them applies to the very next print.
/// </summary>
public sealed class SilentExportSettings
{
    public bool Enabled { get; set; }

    /// <summary>The folder the images go to. Empty = Pictures\CleanPrint. Environment variables (%USERPROFILE%) are expanded.</summary>
    public string Folder { get; set; } = "";

    /// <summary>An <see cref="ImageFormats"/> id: png, jpg, bmp or tif.</summary>
    public string Format { get; set; } = "png";

    /// <summary>The file name, without extension. May contain {date}, {time}, {datetime}, {job}, {page} and {pages} — see <see cref="SilentExportNaming"/>.</summary>
    public string NamePattern { get; set; } = SilentExportNaming.DefaultPattern;

    /// <summary>Whether the job is also left in the job cache, so it can still be opened in CleanPrint later. Off: only the exported files remain.</summary>
    public bool KeepJob { get; set; } = true;

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CleanPrint");

    /// <summary>The folder to write to: the one chosen (variables expanded) or the default.</summary>
    public string EffectiveFolder => Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder.Trim());
}

/// <summary>Loads/saves the silent export settings in a fixed file under the CleanPrint data folder. In Core so the App's Settings dialog and the Watcher read the same file.</summary>
public static class SilentExportSettingsService
{
    private static string FilePath => Path.Combine(CleanPrintPaths.LocalAppDataRoot, "silent-export.json");

    /// <summary>Null if nothing has been saved yet or the file can't be read: silent export is then off.</summary>
    public static SilentExportSettings? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<SilentExportSettings>(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SilentExportSettings settings)
    {
        try
        {
            Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort: losing this isn't worth crashing over
        }
    }
}

/// <summary>Works out the file names silent export uses. Pure, so the rules can be tested.</summary>
public static class SilentExportNaming
{
    public const string DefaultPattern = "CleanPrint-{date}-{time}";
    private const int MaxNameLength = 150;
    private static readonly string[] ReservedNames =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// The file name (no extension) for one page of a job. Tokens, in any case: {date} 2026-10-29, {time} 14-05-33, {datetime} 2026-10-29_14-05-33, {job} the job's id, {page} the page number
    /// (zero-padded to the width of the page count), {pages} the page count. A job of several pages whose pattern has no {page} gets "-{page}" added, so its pages don't overwrite one another.
    /// An empty pattern means the default. Characters a file name can't hold become "_"; names Windows reserves (CON, NUL, COM1...) get a trailing "_".
    /// </summary>
    public static string Build(string? pattern, DateTime now, string jobId, int page, int pageCount)
    {
        string text = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        bool hasPageToken = text.Contains("{page}", StringComparison.OrdinalIgnoreCase);
        if (pageCount > 1 && !hasPageToken) text += "-{page}";

        int width = Math.Max(1, Math.Max(1, pageCount).ToString().Length);
        text = Replace(text, "{datetime}", now.ToString("yyyy-MM-dd_HH-mm-ss"));
        text = Replace(text, "{date}", now.ToString("yyyy-MM-dd"));
        text = Replace(text, "{time}", now.ToString("HH-mm-ss"));
        text = Replace(text, "{job}", jobId);
        text = Replace(text, "{pages}", Math.Max(1, pageCount).ToString());
        text = Replace(text, "{page}", Math.Max(1, page).ToString().PadLeft(width, '0'));
        return Sanitize(text);
    }

    private static string Replace(string text, string token, string value) => text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes text safe to use as a file name.</summary>
    public static string Sanitize(string name)
    {
        // Windows' set, spelled out (rather than Path.GetInvalidFileNameChars, which depends on the OS running the code), plus whatever the OS adds.
        var invalid = new HashSet<char>("\\/:*?\"<>|") ;
        foreach (char c in Path.GetInvalidFileNameChars()) invalid.Add(c);
        var builder = new StringBuilder(name.Length);
        foreach (char c in name) builder.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);
        string clean = builder.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd('.', ' ');
        if (clean.Length == 0) return "CleanPrint";
        if (ReservedNames.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += "_";
        return clean;
    }

    /// <summary>A path in <paramref name="folder"/> that does not exist yet: name.ext, or "name (2).ext", "name (3).ext"... — an export never overwrites an earlier one.</summary>
    public static string UniquePath(string folder, string name, string extension)
    {
        string path = Path.Combine(folder, name + extension);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }
}
