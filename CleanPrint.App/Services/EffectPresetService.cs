using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>
/// Named Effects presets, stored as individual JSON files in a fixed system location
/// (CleanPrintPaths.PresetsRoot) rather than wherever the person happens to browse to via a save
/// dialog — this is what lets the Effects panel offer a simple combo box listing "every preset
/// that exists" and what lets General settings' "apply this preset to newly-loaded jobs" refer to
/// a preset by name alone.
/// </summary>
public static class EffectPresetService
{
    private const string Extension = ".cpeffects";

    private static string PathFor(string presetName) =>
        Path.Combine(CleanPrintPaths.PresetsRoot, SanitizeFileName(presetName) + Extension);

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Every preset name currently saved, alphabetical. Returns an empty list (not an
    /// error) if the presets folder doesn't exist yet — that just means nothing's been saved.</summary>
    public static IReadOnlyList<string> ListPresetNames()
    {
        if (!Directory.Exists(CleanPrintPaths.PresetsRoot)) return Array.Empty<string>();

        return Directory.GetFiles(CleanPrintPaths.PresetsRoot, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Null if the named preset doesn't exist or is unreadable.</summary>
    public static ImageEffectSettings? Load(string presetName)
    {
        try
        {
            string path = PathFor(presetName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ImageEffectSettings>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string presetName, ImageEffectSettings settings)
    {
        Directory.CreateDirectory(CleanPrintPaths.PresetsRoot);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PathFor(presetName), json);
    }

    public static void Delete(string presetName)
    {
        string path = PathFor(presetName);
        if (File.Exists(path)) File.Delete(path);
    }
}
