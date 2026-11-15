using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CleanPrint.Core.Models;

/// <summary>
/// Every customizable keyboard shortcut, stored as a plain string token (e.g. "I", "Ctrl+Left",
/// "OemPlus") parsed/formatted by KeyboardShortcutFormat (CleanPrint.App). Persisted separately
/// from AppSettings (see KeyboardShortcutService) since these are edited via their own dedicated
/// Settings tab rather than alongside the general preferences.
///
/// An empty string means "no shortcut assigned" — MainWindow's key handler simply won't match
/// anything for that action. Defaults below match what was requested; existing fixed shortcuts not
/// in this list (Ctrl+Z undo, Delete, Enter/Escape while drawing a polygon) stay hardcoded, since
/// they're editing-context-specific rather than always-available "major actions".
///
/// Implements INotifyPropertyChanged (a standard BCL interface, not WPF-specific) so the Keyboard
/// Shortcuts settings tab can bind directly to these properties for live, two-way updates.
/// </summary>
public sealed class KeyboardShortcuts : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _openImage = "I";
    public string OpenImage { get => _openImage; set { _openImage = value; OnPropertyChanged(); } }

    private string _openJob = "J";
    public string OpenJob { get => _openJob; set { _openJob = value; OnPropertyChanged(); } }

    private string _zoomIn = "OemPlus";
    public string ZoomIn { get => _zoomIn; set { _zoomIn = value; OnPropertyChanged(); } }

    private string _zoomOut = "OemMinus";
    public string ZoomOut { get => _zoomOut; set { _zoomOut = value; OnPropertyChanged(); } }

    private string _fitWidth = "W";
    public string FitWidth { get => _fitWidth; set { _fitWidth = value; OnPropertyChanged(); } }

    private string _fitHeight = "H";
    public string FitHeight { get => _fitHeight; set { _fitHeight = value; OnPropertyChanged(); } }

    private string _rotateLeft = "Ctrl+Left";
    public string RotateLeft { get => _rotateLeft; set { _rotateLeft = value; OnPropertyChanged(); } }

    private string _rotateRight = "Ctrl+Right";
    public string RotateRight { get => _rotateRight; set { _rotateRight = value; OnPropertyChanged(); } }

    private string _toggleEffects = "E";
    public string ToggleEffects { get => _toggleEffects; set { _toggleEffects = value; OnPropertyChanged(); } }

    private string _settings = "S";
    public string Settings { get => _settings; set { _settings = value; OnPropertyChanged(); } }

    private string _polygon = "P";
    public string Polygon { get => _polygon; set { _polygon = value; OnPropertyChanged(); } }

    private string _rectangle = "R";
    public string Rectangle { get => _rectangle; set { _rectangle = value; OnPropertyChanged(); } }

    private string _print = "Ctrl+P";
    public string Print { get => _print; set { _print = value; OnPropertyChanged(); } }

    private string _export = "X";
    public string Export { get => _export; set { _export = value; OnPropertyChanged(); } }

    // The backtick key (` / ~ — Key.OemTilde, the same enum value as Key.Oem3). Was End when this
    // shortcut first shipped; see SchemaVersion and KeyboardShortcutService.Load for how an already-saved
    // "End" gets moved over.
    private string _finishPolygonOpen = "OemTilde";
    public string FinishPolygonOpen { get => _finishPolygonOpen; set { _finishPolygonOpen = value; OnPropertyChanged(); } }

    /// <summary>Which version of the defaults this saved file was written under — 0 (the value when
    /// the property is absent from an older file) means "from before versioning existed". Lets
    /// KeyboardShortcutService.Load apply a one-time migration when a default changes, instead of
    /// either never updating anyone who already has a saved file (every shortcut is stored explicitly,
    /// so a changed default alone never reaches them) or re-applying the change on every launch (which
    /// would fight a shortcut the person later chose on purpose). Stamped by KeyboardShortcutService.Save,
    /// never edited by hand or via the UI, and deliberately not part of CopyFrom.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Copies every value from another instance into this one, raising the normal
    /// per-property change notifications — same "update in place" pattern as
    /// ImageEffectSettings.CopyFrom, used when loading persisted shortcuts at startup.</summary>
    public void CopyFrom(KeyboardShortcuts other)
    {
        OpenImage = other.OpenImage;
        OpenJob = other.OpenJob;
        ZoomIn = other.ZoomIn;
        ZoomOut = other.ZoomOut;
        FitWidth = other.FitWidth;
        FitHeight = other.FitHeight;
        RotateLeft = other.RotateLeft;
        RotateRight = other.RotateRight;
        ToggleEffects = other.ToggleEffects;
        Settings = other.Settings;
        Polygon = other.Polygon;
        Rectangle = other.Rectangle;
        Print = other.Print;
        Export = other.Export;
        FinishPolygonOpen = other.FinishPolygonOpen;
    }
}
