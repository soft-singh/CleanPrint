using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CleanPrint.App.Services;
using CleanPrint.App.ViewModels;
using CleanPrint.Core.Paths;

namespace CleanPrint.App;

/// <summary>
/// Modal dialog listing every job folder under CleanPrintPaths.JobsRoot (both print-captured jobs
/// and jobs created via "Open Image(s)..." — they're indistinguishable once on disk, both are just
/// a folder of page_*.png files). Lets the person preview a job's first page, reopen it (setting
/// SelectedJobFolder and closing with DialogResult=true), or permanently delete it. The job cache's
/// LIMITS are edited in Settings > General; this dialog only shows current usage (MainViewModel is its
/// DataContext for that) and reports what it measures after a delete.
///
/// Code-behind rather than its own ViewModel: the list/preview state is small, local, and dies with
/// the dialog.
/// </summary>
public partial class JobBrowserWindow : Window
{
    private readonly MainViewModel _viewModel;

    public string? SelectedJobFolder { get; private set; }

    /// <summary>The full, unfiltered list, refreshed from disk — SearchTextBox filters a view of
    /// this rather than re-scanning disk on every keystroke.</summary>
    private List<JobListItem> _allJobs = new();

    /// <summary>Bumped on every selection change so a slow preview load for an earlier selection
    /// can't overwrite the preview of a later one (loads run on a background thread).</summary>
    private int _previewToken;

    // The pages of the job being previewed, and which one is showing (0-based).
    private List<string> _previewPages = new();
    private int _previewIndex;

    public JobBrowserWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => RefreshJobList();
    }

    private void RefreshJobList()
    {
        _allJobs = Directory.Exists(CleanPrintPaths.JobsRoot)
            ? Directory.GetDirectories(CleanPrintPaths.JobsRoot)
                .Select(folder => new JobListItem(folder))
                .OrderByDescending(j => j.LastModified)
                .ToList()
            : new List<JobListItem>();

        ApplySearchFilter();

        // Everything was just enumerated anyway to build the list, so total usage comes for free —
        // handed to the ViewModel, which updates the usage line shown here (JobCacheUsageText) and the
        // main window's cache warning right after a delete, without a second full scan.
        _viewModel.ReportJobCacheUsage(new JobCacheUsage(_allJobs.Count, _allJobs.Sum(j => j.SizeBytes)));
    }

    /// <summary>Filters _allJobs by SearchTextBox's current text (job name or original source
    /// image filename — see JobListItem.MatchesSearch) and repopulates the list.</summary>
    private void ApplySearchFilter()
    {
        JobsListBox.Items.Clear();
        string query = SearchTextBox.Text;
        foreach (var job in _allJobs.Where(j => j.MatchesSearch(query)))
            JobsListBox.Items.Add(job);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplySearchFilter();

    /// <summary>Starts the preview pane on the FIRST page of the selected job, with navigation to the rest. With nothing or several
    /// jobs selected there's no single page to show, so the pane is cleared (with a short note for
    /// the multi-select case). The decode runs off the UI thread at reduced size — a full-resolution
    /// scan is far bigger than a 300px-wide preview needs.</summary>
    private async void JobsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ++_previewToken; // anything still decoding for the previous selection is now stale

        if (JobsListBox.SelectedItems.Count != 1 || JobsListBox.SelectedItem is not JobListItem item)
        {
            _previewPages = new List<string>();
            ShowPreview(null, JobsListBox.SelectedItems.Count > 1 ? $"{JobsListBox.SelectedItems.Count} jobs selected" : "");
            UpdatePreviewNav();
            return;
        }

        _previewPages = CleanPrintPaths.GetJobPageFiles(item.FolderPath).ToList();
        _previewIndex = 0;
        if (_previewPages.Count == 0)
        {
            ShowPreview(null, "This job has no pages.");
            UpdatePreviewNav();
            return;
        }
        await ShowPreviewPageAsync();
    }

    /// <summary>Decodes and shows the page at <see cref="_previewIndex"/>. A newer request (another page, another job) supersedes one still decoding.</summary>
    private async Task ShowPreviewPageAsync()
    {
        if (_previewPages.Count == 0) { UpdatePreviewNav(); return; }
        _previewIndex = Math.Clamp(_previewIndex, 0, _previewPages.Count - 1);
        int token = ++_previewToken;
        string path = _previewPages[_previewIndex];
        UpdatePreviewNav();

        // With the navigation strip showing there is no room for (or need of) a status line: the old page just stays until the new one is ready.
        if (_previewPages.Count <= 1) SetCaption("Loading preview...");
        var thumbnail = await Task.Run(() => LoadThumbnail(path));
        if (token != _previewToken) return; // moved on while this was decoding

        ShowPreview(thumbnail, thumbnail is null
            ? "Preview unavailable."
            : _previewPages.Count > 1 ? "" : "1 page");
    }

    /// <summary>Shows or hides the navigation strip (only for a job of more than one page) and brings its page box and buttons up to date.</summary>
    private void UpdatePreviewNav()
    {
        bool several = _previewPages.Count > 1;
        PreviewNav.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        if (!several) return;

        PreviewPageBox.Text = (_previewIndex + 1).ToString();
        PreviewPageOfText.Text = $"of {_previewPages.Count}";
        PreviewFirstButton.IsEnabled = PreviewPrevButton.IsEnabled = _previewIndex > 0;
        PreviewNextButton.IsEnabled = PreviewLastButton.IsEnabled = _previewIndex < _previewPages.Count - 1;
    }

    private async Task GoToPreviewPageAsync(int index)
    {
        if (_previewPages.Count == 0) return;
        int clamped = Math.Clamp(index, 0, _previewPages.Count - 1);
        if (clamped == _previewIndex) { UpdatePreviewNav(); return; } // also snaps a mistyped number back
        _previewIndex = clamped;
        await ShowPreviewPageAsync();
    }

    private async void PreviewFirst_Click(object sender, RoutedEventArgs e) => await GoToPreviewPageAsync(0);
    private async void PreviewPrev_Click(object sender, RoutedEventArgs e) => await GoToPreviewPageAsync(_previewIndex - 1);
    private async void PreviewNext_Click(object sender, RoutedEventArgs e) => await GoToPreviewPageAsync(_previewIndex + 1);
    private async void PreviewLast_Click(object sender, RoutedEventArgs e) => await GoToPreviewPageAsync(_previewPages.Count - 1);

    /// <summary>The page box: Enter (or leaving it) goes to the typed page; anything that isn't a number puts the real page number back.</summary>
    private async void PreviewPageBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // the dialog's own Enter is "Open" — not wanted here
        await CommitPreviewPageBoxAsync();
    }

    private async void PreviewPageBox_LostFocus(object sender, RoutedEventArgs e) => await CommitPreviewPageBoxAsync();

    private async Task CommitPreviewPageBoxAsync()
    {
        if (int.TryParse(PreviewPageBox.Text.Trim(), out int typed)) await GoToPreviewPageAsync(typed - 1);
        else UpdatePreviewNav();
    }

    /// <summary>The mouse wheel over the page turns pages: up for the previous, down for the next.</summary>
    private async void PreviewArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_previewPages.Count < 2) return;
        e.Handled = true;
        await GoToPreviewPageAsync(_previewIndex + (e.Delta > 0 ? -1 : 1));
    }

    private void ShowPreview(BitmapSource? image, string caption)
    {
        PreviewImageControl.Source = image;
        PreviewPage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        SetCaption(caption);
    }

    /// <summary>The line under the page is only for what the navigation strip can't say (no pages, couldn't load, several jobs selected, a one-page job). Where the strip is
    /// showing, "Page 3 of 5" would just repeat it — so the line is taken out of the layout altogether rather than left blank.</summary>
    private void SetCaption(string text)
    {
        PreviewCaption.Text = text;
        PreviewCaption.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Decodes at a reduced width. CacheOption.OnLoad matters beyond speed: it fully reads
    /// the file and releases it immediately, so a previewed job's page file is never left locked —
    /// which would make Delete fail on exactly the job just looked at.</summary>
    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = 640;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // unreadable/corrupt page file — the caller shows "Preview unavailable."
        }
    }

    private void OpenSelected()
    {
        if (JobsListBox.SelectedItem is not JobListItem item) return;
        SelectedJobFolder = item.FolderPath;
        DialogResult = true;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void JobsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedJobs();

    /// <summary>Delete key in the job list: same confirmation and deletion as the Delete button. Only reacts to
    /// keys pressed while the LIST has focus, so Delete still just edits text in the search box.</summary>
    private void JobsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || JobsListBox.SelectedItems.Count == 0) return;
        e.Handled = true;
        DeleteSelectedJobs();
    }

    /// <summary>Confirms, then deletes the selected job(s). Afterwards the highlight moves to the job that took
    /// the place of the (first) deleted one — the next one down, or the new last one if it was at the end — and
    /// the list keeps keyboard focus, so a run of unwanted jobs can be cleared by just pressing Delete repeatedly.</summary>
    private void DeleteSelectedJobs()
    {
        var items = JobsListBox.SelectedItems.Cast<JobListItem>().ToList();
        if (items.Count == 0) return;

        // Where the highlight goes afterwards: the position of the topmost selected row.
        int nextIndex = items.Min(item => JobsListBox.Items.IndexOf(item));

        string header = items.Count == 1
            ? $"Permanently delete \"{items[0].DisplayName}\" ({items[0].PageCount} page(s))? This cannot be undone."
            : $"Permanently delete these {items.Count} jobs? This cannot be undone.";
        var itemLines = items.Count == 1
            ? Enumerable.Empty<string>() // the single job's name is already in the header — no need to repeat it in the list below
            : items.Select(i => $"• {i.DisplayName} ({i.PageCount} page(s))");

        var confirmDialog = new ScrollableMessageDialog(
            items.Count == 1 ? "Delete Job" : "Delete Jobs", header, itemLines) { Owner = this };
        if (confirmDialog.ShowDialog() != true || !confirmDialog.Result) return;

        var failures = new List<string>();
        foreach (var item in items)
        {
            try
            {
                Directory.Delete(item.FolderPath, recursive: true);
            }
            catch (Exception ex)
            {
                failures.Add($"{item.DisplayName}: {ex.Message}");
            }
        }

        RefreshJobList();
        SelectAndFocusRow(nextIndex);

        if (failures.Count > 0)
        {
            var failureDialog = new ScrollableMessageDialog(
                "Delete Failed", $"Could not delete {failures.Count} job(s):", failures, showCancelButton: false) { Owner = this };
            failureDialog.ShowDialog();
        }
    }

    /// <summary>Highlights the row at the index (clamped to the list), scrolls it into view and gives it keyboard
    /// focus. Focus is applied after layout (Loaded priority) because the rows were just rebuilt and their
    /// containers don't exist yet.</summary>
    private void SelectAndFocusRow(int index)
    {
        if (JobsListBox.Items.Count == 0) return;
        index = Math.Clamp(index, 0, JobsListBox.Items.Count - 1);

        JobsListBox.SelectedIndex = index;
        JobsListBox.ScrollIntoView(JobsListBox.SelectedItem);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (JobsListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem row)
                row.Focus();
            else
                JobsListBox.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>One row in the job list — computed once when the list is (re)built, not live-bound,
/// since job folders only change via actions this same dialog already refreshes after.</summary>
public sealed class JobListItem
{
    public string FolderPath { get; }
    public string DisplayName { get; }
    public int PageCount { get; }
    public DateTime LastModified { get; }
    public string DisplayText { get; }

    /// <summary>Total size of everything in the job's folder, in bytes (what counts toward the
    /// job cache's size limit), and the same formatted for the list's Size column.</summary>
    public long SizeBytes { get; }
    public string SizeText { get; }

    /// <summary>Original source image filename(s), if this job was created via "Open Image(s)" —
    /// empty for print-captured jobs. Just the filenames (not full paths), for display/search.</summary>
    public IReadOnlyList<string> SourceImageNames { get; }

    public JobListItem(string folderPath)
    {
        FolderPath = folderPath;
        DisplayName = Path.GetFileName(folderPath);
        PageCount = CleanPrintPaths.GetJobPageFiles(folderPath).Count;
        LastModified = Directory.GetLastWriteTime(folderPath);
        SizeBytes = JobCacheService.GetFolderSize(folderPath);
        SizeText = JobCacheService.FormatSize(SizeBytes);
        DisplayText = $"{DisplayName}      {PageCount} page(s)      last modified {LastModified:g}";

        var sourceInfo = JobSourceService.Load(folderPath);
        SourceImageNames = sourceInfo?.SourceImagePaths
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList() ?? new List<string>();
    }

    /// <summary>True if the query is empty, or matches the job's own name or any of its source
    /// image filenames (case-insensitive substring).</summary>
    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return SourceImageNames.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
