using CleanPrint.Core.Export;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CleanPrint.App.Services;
using CleanPrint.App.ViewModels;
using CleanPrint.Core.Models;

namespace CleanPrint.App;

/// <summary>
/// General settings (default preset applied to newly-loaded jobs, Ctrl+scroll zoom sensitivity)
/// and Keyboard Shortcuts (every customizable shortcut — see KeyboardShortcuts). Page Setup
/// (margins, page numbers) lives in the Split &amp; Print dialog instead, as its own tab there
/// alongside the split/print controls it's more directly related to using.
///
/// DataContext is the MainViewModel passed in directly, so bindings here (including
/// Shortcuts.OpenImage etc., since Shortcuts is a property on MainViewModel) work the same way
/// they do everywhere else in this app that binds straight to it.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly MainViewModel _viewModel;
    private const string NoneOption = "(None)";

    public SettingsDialog(MainViewModel viewModel)
    {
        // The silent-export folder box shows the real default rather than looking empty. (An empty folder means that default anyway; this just makes it visible.)
        if (string.IsNullOrWhiteSpace(viewModel.SilentExport.Folder))
            viewModel.SilentExport.Folder = SilentExportSettings.DefaultFolder;

        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        UpdateSilentExample();

        // Refresh the job cache usage line shown in the Job cache section (usually already measured at
        // startup/job load, but it may be stale if jobs were deleted or added since).
        _ = _viewModel.RefreshJobCacheUsageAsync();

        DefaultPresetComboBox.Items.Add(NoneOption);
        foreach (var name in EffectPresetService.ListPresetNames())
            DefaultPresetComboBox.Items.Add(name);

        DefaultPresetComboBox.SelectedItem = string.IsNullOrEmpty(_viewModel.DefaultPresetName)
            ? NoneOption
            : DefaultPresetComboBox.Items.Cast<string>().FirstOrDefault(n => n == _viewModel.DefaultPresetName) ?? NoneOption;

        // Shortcuts are edited far less often than most settings, so they're saved once when the
        // dialog closes (any way it closes — Close button, [X], Escape) rather than debounced on
        // every individual recording the way EffectSettings/Zoom are.
        Closing += (_, _) =>
        {
            _viewModel.SaveShortcuts();
            _viewModel.SaveExportQuality();
            _viewModel.SaveSilentExport();
        };
    }

    /// <summary>Icon colour: back to the theme's own (slider to 0).</summary>
    private void ResetIconLightnessButton_Click(object sender, RoutedEventArgs e) => _viewModel.IconLightness = 0;

    /// <summary>Choose the silent-export folder.</summary>
    private void BrowseSilentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder silent export saves to",
            InitialDirectory = Directory.Exists(_viewModel.SilentExport.EffectiveFolder) ? _viewModel.SilentExport.EffectiveFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        SilentFolderBox.Text = dialog.FolderName;   // the binding carries it to the settings
        UpdateSilentExample();
    }

    private void SilentExportInput_Changed(object sender, EventArgs e) => UpdateSilentExample();

    /// <summary>The line under the file-name box that shows what a file would actually be called, so the pattern can be seen working.</summary>
    private void UpdateSilentExample()
    {
        if (SilentExampleText is null) return;
        var settings = _viewModel.SilentExport;
        var format = ImageFormats.Find(ImageFormats.Silent, settings.Format);
        string first = SilentExportNaming.Build(settings.NamePattern, DateTime.Now, "155307-20261029-141500", 1, 3) + format.Extension;
        SilentExampleText.Text = $"For a 3-page job the first file would be called:  {first}";
    }

    private void DefaultPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultPresetComboBox.SelectedItem is not string selected) return;
        _viewModel.DefaultPresetName = selected == NoneOption ? null : selected;
    }

    private void ResetShortcutsButton_Click(object sender, RoutedEventArgs e) => _viewModel.Shortcuts.CopyFrom(new KeyboardShortcuts());

    private void AboutButton_Click(object sender, RoutedEventArgs e) => new AboutDialog { Owner = this }.ShowDialog();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
