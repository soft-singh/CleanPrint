using System;
using System.Collections.Generic;
using System.Windows.Input;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>
/// Converts between the plain string tokens KeyboardShortcuts stores (e.g. "Ctrl+Left", "I",
/// "OemPlus") and WPF's (ModifierKeys, Key) pair, in both directions, plus a display-friendly
/// rendering for the Settings UI. Kept as one small, self-contained helper so the token FORMAT
/// itself only needs to be understood in exactly one place.
/// </summary>
public static class KeyboardShortcutFormat
{
    /// <summary>Every customizable shortcut action: its C# property name (matches
    /// KeyboardShortcuts exactly, e.g. nameof(KeyboardShortcuts.OpenImage)), a display name for the
    /// UI, and get/set delegates. A hardcoded list rather than reflection — there are only 14 of
    /// these, and reflection would need extra work to attach sensible display names anyway. Used by
    /// ShortcutBox for conflict-checking: before committing a newly-recorded shortcut, it scans
    /// every OTHER action here for the same token.</summary>
    public static readonly IReadOnlyList<(string PropertyName, string DisplayName, Func<KeyboardShortcuts, string> Get, Action<KeyboardShortcuts, string> Set)> AllActions =
        new (string, string, Func<KeyboardShortcuts, string>, Action<KeyboardShortcuts, string>)[]
        {
            (nameof(KeyboardShortcuts.OpenImage), "Open Image(s)", s => s.OpenImage, (s, v) => s.OpenImage = v),
            (nameof(KeyboardShortcuts.OpenJob), "Open Job", s => s.OpenJob, (s, v) => s.OpenJob = v),
            (nameof(KeyboardShortcuts.ZoomIn), "Zoom In", s => s.ZoomIn, (s, v) => s.ZoomIn = v),
            (nameof(KeyboardShortcuts.ZoomOut), "Zoom Out", s => s.ZoomOut, (s, v) => s.ZoomOut = v),
            (nameof(KeyboardShortcuts.FitWidth), "Fit Width", s => s.FitWidth, (s, v) => s.FitWidth = v),
            (nameof(KeyboardShortcuts.FitHeight), "Fit Height", s => s.FitHeight, (s, v) => s.FitHeight = v),
            (nameof(KeyboardShortcuts.RotateLeft), "Rotate Left", s => s.RotateLeft, (s, v) => s.RotateLeft = v),
            (nameof(KeyboardShortcuts.RotateRight), "Rotate Right", s => s.RotateRight, (s, v) => s.RotateRight = v),
            (nameof(KeyboardShortcuts.ToggleEffects), "Toggle Effects", s => s.ToggleEffects, (s, v) => s.ToggleEffects = v),
            (nameof(KeyboardShortcuts.Settings), "Settings", s => s.Settings, (s, v) => s.Settings = v),
            (nameof(KeyboardShortcuts.Polygon), "Polygon", s => s.Polygon, (s, v) => s.Polygon = v),
            (nameof(KeyboardShortcuts.Rectangle), "Rectangle", s => s.Rectangle, (s, v) => s.Rectangle = v),
            (nameof(KeyboardShortcuts.Print), "Print", s => s.Print, (s, v) => s.Print = v),
            (nameof(KeyboardShortcuts.Export), "Export Current Page", s => s.Export, (s, v) => s.Export = v),
            (nameof(KeyboardShortcuts.FinishPolygonOpen), "Finish Polygon (Leave Open)", s => s.FinishPolygonOpen, (s, v) => s.FinishPolygonOpen = v),
        };

    /// <summary>True if the two tokens represent the same combination — used for conflict
    /// checking, so it goes through the same Parse-based comparison Matches uses (rather than a
    /// naive string equality, which would miss e.g. "Ctrl+Shift+P" vs "Shift+Ctrl+P" being the
    /// same shortcut).</summary>
    public static bool TokensEqual(string? a, string? b)
    {
        var (modsA, keyA) = Parse(a);
        var (modsB, keyB) = Parse(b);
        if (keyA == Key.None || keyB == Key.None) return false; // an unset shortcut never "conflicts"
        return modsA == modsB && NormalizeKeyToken(keyA) == NormalizeKeyToken(keyB);
    }

    /// <summary>The key a person actually pressed, from a live key event. Three cases report something
    /// other than the real key in e.Key: Alt-held keys arrive as Key.System (real key in SystemKey),
    /// and keys an IME or a DEAD-KEY layout is holding arrive as Key.ImeProcessed / Key.DeadCharProcessed
    /// (real key in ImeProcessedKey / DeadCharProcessedKey). The dead-key case matters for the backtick
    /// shortcut specifically: on layouts like US-International, ` is a dead key (it waits to combine with
    /// the next letter into an accented one), so without this it would never match its shortcut.</summary>
    public static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    /// <summary>Builds the stored token from a live key event's modifiers/key — used by
    /// ShortcutBox while recording a new shortcut. Numpad +/- are normalized to the same tokens as
    /// the main-row +/- keys (OemPlus/OemMinus), so either physical key can be used interchangeably
    /// both when recording and when matching later.</summary>
    public static string FromKeyEvent(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(NormalizeKeyToken(key));
        return string.Join("+", parts);
    }

    /// <summary>Parses a stored token back into (modifiers, key) for matching against incoming key
    /// events. Returns (None, Key.None) for an empty/unset shortcut, which never matches anything.</summary>
    public static (ModifierKeys Modifiers, Key Key) Parse(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return (ModifierKeys.None, Key.None);

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                default:
                    if (Enum.TryParse<Key>(part, ignoreCase: true, out var parsed))
                        key = parsed;
                    break;
            }
        }

        return (modifiers, key);
    }

    /// <summary>True if the given live modifiers/key (from a KeyEventArgs, with numpad +/- and
    /// Alt-routed System keys already normalized by the caller) matches the stored shortcut.</summary>
    public static bool Matches(string? shortcut, ModifierKeys liveModifiers, Key liveKey)
    {
        var (modifiers, key) = Parse(shortcut);
        if (key == Key.None) return false; // unset shortcut never matches
        return modifiers == liveModifiers && NormalizeKeyToken(key) == NormalizeKeyToken(liveKey);
    }

    /// <summary>Human-readable rendering for the Settings UI, e.g. "Ctrl+Left", "+", "(none)".</summary>
    public static string ToDisplayString(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return "(none)";
        return shortcut.Replace("OemPlus", "+").Replace("OemMinus", "-").Replace("OemTilde", "`").Replace("Oem3", "`");
    }

    /// <summary>Numpad +/- collapse to the same token as the main-row +/- keys, so a shortcut
    /// recorded with one matches a press of the other.</summary>
    private static string NormalizeKeyToken(Key key) => key switch
    {
        Key.OemPlus or Key.Add => "OemPlus",
        Key.OemMinus or Key.Subtract => "OemMinus",
        _ => key.ToString(),
    };
}
