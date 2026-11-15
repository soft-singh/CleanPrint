using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CleanPrint.App.ViewModels;
using Microsoft.Win32;

namespace CleanPrint.App;

/// <summary>
/// Split/print controls, moved out of MainWindow's own layout (ver_0.3.0) into their own non-modal
/// window — frees the entire main window for the image preview, and removes the need to
/// collapse/expand a big controls section that was only relevant when actually printing/exporting.
/// Two OUTER tabs: "Split &amp; Print" and "Page Setup" (margins, page numbers — moved here from
/// SettingsDialog, since they're more directly related to split/print than to SettingsDialog's own
/// remaining General content). "Split &amp; Print" itself is laid out printer/copies, page settings,
/// then a "Print Mode" group with its OWN inner tabs — "Size" (whole page, scaled/fitted) and
/// "Split" (tiled) — inspired by a conventional desktop print dialog's layout, with a live preview
/// of split tiles alongside it.
///
/// DataContext is the MainViewModel passed in directly — the same instance MainWindow itself uses
/// — so every binding here (Binding SplitRows, PrinterNames, etc.) works exactly as it did when
/// this markup lived in MainWindow.xaml.
/// </summary>
public partial class PrintDialog : Window
{
    private readonly MainViewModel _viewModel;

    public PrintDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Reflects whatever PrintWholePage was last left at (e.g. restored from AppSettings) —
        // without this, the inner tab would always start on "Size" regardless of that value,
        // silently resetting it back to true the next time PrintModeTabControl_SelectionChanged
        // fires from the tab's own initial selection.
        PrintModeTabControl.SelectedIndex = _viewModel.PrintWholePage ? 0 : 1;

        // The preview starts on whichever page the main window is showing each time the dialog is
        // (re)opened. Rendering itself is triggered by Activated below (showing a window activates
        // it), with a post-layout safety net here in case that doesn't fire — the check keeps the
        // two from ever double-rendering.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            _viewModel.SyncPrintPreviewToCurrentPage();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (IsVisible && _viewModel.PrintWholePage && _viewModel.PrintPreviewImage is null && !_viewModel.IsPrintPreviewRendering)
                    _ = _viewModel.RefreshPrintPreviewAsync();
            }));
        };

        // This dialog is non-modal, so effects/polygons/hidden areas can change in the main window
        // while it's open. Coming back to it is exactly when the preview needs to catch up —
        // re-rendering on activation is far cheaper than hooking every kind of edit individually.
        Activated += (_, _) =>
        {
            if (_viewModel.PrintWholePage)
                _ = _viewModel.RefreshPrintPreviewAsync();
        };
    }

    /// <summary>The Print Mode area's own two tabs — Size and Split — ARE PrintWholePage's value
    /// now, rather than a separate pair of radio buttons duplicating the same choice: Size means
    /// "print the whole page" (true), Split means "print whatever's been split" (false). Guarded
    /// against bubbled SelectionChanged events from anything nested inside a tab's own content
    /// (Selector.SelectionChanged is shared by ComboBox/ListBox too, and bubbles), since only a
    /// change to THIS TabControl's own selection should ever update PrintWholePage.</summary>
    private void PrintModeTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PrintModeTabControl)) return;
        if (PrintModeTabControl.SelectedItem is TabItem selected)
            _viewModel.PrintWholePage = Equals(selected.Header, "Size");

        // Switching back to Size: the preview may be stale (or never rendered). IsVisible guard so
        // the constructor's own initial tab selection doesn't kick off a render for a hidden dialog.
        if (IsVisible && _viewModel.PrintWholePage)
            _ = _viewModel.RefreshPrintPreviewAsync();
    }

    /// <summary>Issues the print command, then closes the dialog immediately rather than waiting
    /// for the print job to fully finish — printing can take a while for many pages, and progress/
    /// completion is already visible via the status line (here and in MainWindow) without needing
    /// the dialog to stay open. "Close" is intercepted by MainWindow's Closing handler and turned
    /// into a Hide, so this doesn't actually destroy the dialog — same as the window's own [X].</summary>
    private void PrintAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PrintAllCommand.CanExecute(null))
            _viewModel.PrintAllCommand.Execute(null);
        Close();
    }

    /// <summary>Exports the current split tiles — unlike PrintAllButton_Click, this deliberately
    /// does NOT close the dialog afterward, since exporting is often one step in an iterative
    /// "adjust split settings, export, check the result, adjust again" workflow rather than a
    /// final action the way printing is.</summary>
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export",
            Filter = "PNG Image|*.png|JPEG Image|*.jpg",
            FilterIndex = _viewModel.DefaultExportFilterIndex,   // Settings > Capture & Export > Default image format
            FileName = "export",
        };
        if (dialog.ShowDialog(this) != true) return;

        string format = Path.GetExtension(dialog.FileName).TrimStart('.').ToLowerInvariant();

        try
        {
            await _viewModel.ExportAsync(dialog.FileName, format);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>First/Last page of the PREVIEW (not the main window) — GoToPrintPreviewPageAsync
    /// clamps out-of-range values, so these need no bounds checking here. Previous/Next are
    /// Command bindings in the XAML.</summary>
    private void FirstPageButton_Click(object sender, RoutedEventArgs e) => _ = _viewModel.GoToPrintPreviewPageAsync(1);

    private void LastPageButton_Click(object sender, RoutedEventArgs e) => _ = _viewModel.GoToPrintPreviewPageAsync(_viewModel.PrintPreviewPageCount);

    /// <summary>The page box commits on LostFocus (see its binding), so Enter needs to push the typed
    /// value through explicitly. Handled here so Enter doesn't also fall through to anything else.</summary>
    private void PageNumberTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    /// <summary>Clicking the previewed page flips Portrait/Landscape — for that page's own settings if it has any, otherwise the common ones
    /// the Orientation radio buttons bind to, so the two stay in sync automatically.</summary>
    private void PreviewSheet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.TogglePreviewOrientation();
        e.Handled = true;
    }

    /// <summary>Escape closes the dialog (intercepted by MainWindow's Closing handler and turned
    /// into a Hide — see PrintButton_Click's own doc comment — so this doesn't destroy the dialog).
    /// Enter prints, EXCEPT while a text field has focus (the page-number box, a NumericUpDown's
    /// inner TextBox, the page-range box) — otherwise confirming a typed value there would also
    /// unexpectedly trigger a print.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is not TextBox)
        {
            PrintAllButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
