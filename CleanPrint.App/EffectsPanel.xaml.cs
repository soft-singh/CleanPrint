using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CleanPrint.App.Services;
using CleanPrint.Core.Models;

namespace CleanPrint.App;

/// <summary>
/// The actual Effects controls (background whitening, brightness, contrast, etc.) as a reusable
/// UserControl rather than a Window, so the exact same instance can live either embedded in
/// MainWindow's own layout (docked) or as the Content of a separate floating Window (undocked) —
/// see MainWindow.xaml.cs's ShowDockedPanel/ToggleEffectsDock for how it's moved between the two.
///
/// DataContext is the ImageEffectSettings instance passed in directly — the SAME one MainViewModel
/// holds and reprocesses from on every change — so every slider/checkbox in the XAML binds straight
/// to it; no separate ViewModel needed for this control.
/// </summary>
public partial class EffectsPanel : UserControl
{
    private readonly ImageEffectSettings _settings;

    /// <summary>True only while a preset is actively being applied (via the combo, or Reset) —
    /// guards the _settings.PropertyChanged subscription below so applying a preset's dozen
    /// individual property changes doesn't immediately clear the combo selection it just set.</summary>
    private bool _isApplyingPreset;

    /// <summary>Raised when the Dock/Undock button is clicked. This control doesn't know whether
    /// it's currently docked or floating, or how to move itself between the two — only MainWindow
    /// (which owns both the docked host column and any floating window) knows that, so it decides
    /// what "toggle" actually means right now.</summary>
    public event EventHandler? DockToggleRequested;

    public EffectsPanel(ImageEffectSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // The current settings may already match a saved preset (e.g. a default preset was just
        // applied to a newly-loaded job before this panel was ever created) — check for a match so
        // the combo reflects that instead of showing blank despite a preset genuinely being active.
        // Computed BEFORE DataContext is assigned below: assigning it is what makes every slider/
        // number box bind and push its first value, and the match should be judged against the
        // settings exactly as they were handed to this panel, not as the controls' initial binding
        // pass leaves them.
        string? initialMatch = FindMatchingPresetName();
        DataContext = settings;
        RefreshPresetList(selectName: initialMatch);

        // Two different reactions depending on HOW settings changed: an individual edit (e.g.
        // dragging a slider) should immediately clear the combo's selection, but a bulk load (e.g.
        // switching to a different job, which applies ITS OWN default preset or restored state)
        // should instead re-check whether the newly-loaded values happen to match a saved preset —
        // BulkChanged (raised once per CopyFrom call, after every property has its new value)
        // distinguishes this from PropertyChanged (raised many times during the very same call).
        _settings.PropertyChanged += Settings_PropertyChanged;
        _settings.BulkChanged += Settings_BulkChanged;
    }

    /// <summary>Null if the current settings don't happen to match any saved preset's values.</summary>
    private string? FindMatchingPresetName() =>
        EffectPresetService.ListPresetNames().FirstOrDefault(name => EffectPresetService.Load(name)?.HasSameValues(_settings) == true);

    /// <summary>Keeps the Delete button's enabled state in sync with whether a preset is actually
    /// selected — called from every place that changes PresetComboBox's selection, since several of
    /// them (RefreshPresetList, Settings_PropertyChanged, Settings_BulkChanged, ResetButton_Click)
    /// deliberately unsubscribe PresetComboBox_SelectionChanged before changing it (to avoid
    /// re-triggering a preset load as a side effect), so that handler alone can't be relied on to
    /// catch every case.</summary>
    private void UpdateDeleteButtonState() => DeletePresetButton.IsEnabled = PresetComboBox.SelectedItem is not null;

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingPreset) return;
        if (PresetComboBox.SelectedItem is null) return;

        PresetComboBox.SelectionChanged -= PresetComboBox_SelectionChanged;
        PresetComboBox.SelectedItem = null;
        PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;
        UpdateDeleteButtonState();
    }

    private void Settings_BulkChanged(object? sender, EventArgs e)
    {
        // _isApplyingPreset covers the combo's own selection (already correct) and Reset (already
        // explicitly cleared) — only a job switch reaches here with it false, since that's driven
        // by MainViewModel.LoadJob rather than anything in this panel.
        if (_isApplyingPreset) return;

        PresetComboBox.SelectionChanged -= PresetComboBox_SelectionChanged;
        PresetComboBox.SelectedItem = FindMatchingPresetName();
        PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;
        UpdateDeleteButtonState();
    }

    /// <summary>Updates the toggle button's label to reflect where the panel currently lives —
    /// called by MainWindow right after it actually moves the panel.</summary>
    /// <summary>Width of the splitter strip MainWindow puts between the docked panel and the preview (it is
    /// transparent now, so it reads as empty space — see PanelRoot's comment in the XAML).</summary>
    public const double DockedSplitterStripWidth = 5;

    /// <summary>Gap between the scrollbar and the panel's right edge. The scrollbar used to be 17 wide and the panel left 12; now it is a slim 8, so 4 is plenty. (With the 8 the content
    /// keeps clear of the bar, content ends 12 from the edge when there is no scrollbar at all — the same as on the left.)</summary>
    public const double RightEdgeGap = 4;

    public void SetDockedState(bool isDocked)
    {
        DockToggleButton.Content = isDocked ? "Undock" : "Dock to Main Window";

        // Docked, the splitter strip supplies part of the right-hand gap itself.
        double rootRight = Math.Max(0, RightEdgeGap - (isDocked ? DockedSplitterStripWidth : 0));
        PanelRoot.Margin = new Thickness(12, 12, rootRight, 12);

        // Only the SCROLLBAR goes close to the edge. The preset row at the top and the Undock / Reset buttons at the bottom are ordinary controls and keep their 12 units from the edge, like the
        // left-hand side: each gets whatever the panel's own margin (and the splitter strip) don't already supply.
        double toEdge = 12 - (rootRight + (isDocked ? DockedSplitterStripWidth : 0));
        PresetRow.Margin = new Thickness(0, 10, toEdge, 10);
        FooterRow.Margin = new Thickness(0, 12, toEdge, 0);
    }

    private void DockToggleButton_Click(object sender, RoutedEventArgs e) => DockToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>For hosting the panel somewhere that crops by hand (the base image editor): the Auto-Crop section would only confuse there, so it is taken out of the layout.
    /// (The settings it edits are still there, and ignored by whoever is using them.)</summary>
    public void HideAutoCropSection() => AutoCropGroup.Visibility = Visibility.Collapsed;

    /// <summary>For hosting the panel somewhere it can't be docked or undocked (the base image editor): removes the Dock/Undock button.</summary>
    public void HideDockToggle() => DockToggleButton.Visibility = Visibility.Collapsed;

    /// <summary>Re-reads every preset name from the fixed presets folder and repopulates the combo
    /// — called after saving a new one so it shows up immediately, and once at startup.</summary>
    private void RefreshPresetList(string? selectName)
    {
        PresetComboBox.SelectionChanged -= PresetComboBox_SelectionChanged; // avoid firing a load while just repopulating
        PresetComboBox.Items.Clear();
        foreach (var name in EffectPresetService.ListPresetNames())
            PresetComboBox.Items.Add(name);
        PresetComboBox.SelectedItem = selectName;
        PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;
        UpdateDeleteButtonState();
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDeleteButtonState();
        if (PresetComboBox.SelectedItem is not string presetName) return;

        var loaded = EffectPresetService.Load(presetName);
        if (loaded is null) return;

        _isApplyingPreset = true;
        try
        {
            _settings.CopyFrom(loaded); // updates the SAME instance in place, so MainViewModel's live reprocess fires normally
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    /// <summary>Prompts for a name — typing a brand new one saves a new preset; picking one of the
    /// existing names shown (via TextInputDialog's optional overwrite list) saves over it. That same
    /// list also offers to delete any of those existing presets outright (see
    /// TextInputDialog.SetExistingValues' onDeleteRequested parameter).</summary>
    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        string currentName = PresetComboBox.SelectedItem as string ?? "";
        var dialog = new TextInputDialog("Save Preset", "Preset name (or choose an existing one below to overwrite it):", currentName)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.SetExistingValues(EffectPresetService.ListPresetNames(), onDeleteRequested: EffectPresetService.Delete);
        bool saved = dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText);
        if (saved)
            EffectPresetService.Save(dialog.InputText, _settings);

        // Refresh regardless of whether a save happened — a preset may have been deleted from
        // within the dialog even if it was otherwise cancelled, so the combo needs to drop it either
        // way. Re-select whatever the person just typed/saved when that happened; otherwise keep
        // whatever was already selected, as long as it wasn't the very thing just deleted.
        RefreshPresetList(selectName: saved ? dialog.InputText : PresetComboBox.SelectedItem as string);
    }

    /// <summary>Deletes whichever preset is currently selected in the combo — no-ops if nothing is
    /// selected (the button is disabled in that case, but this guards it too in case that state
    /// ever gets out of sync).</summary>
    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not string presetName) return;

        var confirm = MessageBox.Show(
            $"Delete preset \"{presetName}\"? This cannot be undone.",
            "Delete Preset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        EffectPresetService.Delete(presetName);
        RefreshPresetList(selectName: null);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _isApplyingPreset = true;
        try
        {
            _settings.CopyFrom(new ImageEffectSettings());
        }
        finally
        {
            _isApplyingPreset = false;
        }

        PresetComboBox.SelectionChanged -= PresetComboBox_SelectionChanged;
        PresetComboBox.SelectedItem = null; // resetting no longer corresponds to whichever preset was selected, if any
        PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;
        UpdateDeleteButtonState();
    }
}
