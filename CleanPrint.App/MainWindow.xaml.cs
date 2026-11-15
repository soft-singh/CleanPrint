using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using CleanPrint.App.Services;
using CleanPrint.App.ViewModels;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;
using Microsoft.Win32;

namespace CleanPrint.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.PasteTextLocationProvider = GetViewportCenterImageSpace;
        // The floating text toolbar must start where the preview does, which moves as the Effects panel docks, undocks and is
        // resized by its splitter — all of which change the dock host's width.
        EffectsDockHost.SizeChanged += (_, _) => UpdateTextOptionsBarMargin();

        ApplySavedWindowPlacement();
        UpdateWindowStateVisuals(); // the saved placement may have restored the window maximized
        Closing += (_, _) =>
        {
            SaveWindowPlacement();
            AppSettingsService.Save(ViewModel.BuildSettingsSnapshot());
            ViewModel.SaveCurrentJobState();
        };

        // Freeze Row's pinned overlay needs repositioning whenever the page or zoom changes (in
        // addition to ScrollChanged, which covers actual scrolling/panning) — see
        // UpdateFrozenOverlayPosition.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        PreviewScrollViewer.PreviewMouseDown += PreviewScrollViewer_PreviewMouseDownMiddle;
        PreviewScrollViewer.PreviewMouseUp += PreviewScrollViewer_PreviewMouseUpMiddle;
        Loaded += (_, _) =>
        {
            UpdateWindowStateVisuals();
            UpdateFrozenOverlayPosition();
            UpdateJobNavigationButtonStates();
            _ = ViewModel.RefreshJobCacheUsageAsync(); // so a cache that's already over a limit warns right at startup
        };
    }

    // ---- The Color and Fill boxes: a safety net -------------------------------------------------------------------------------------
    // After some sequences (double-click a text, edit it, click away) the Color box was left EMPTY and stayed empty, although the view model was answering correctly: a ComboBox whose list is
    // swapped while it is part-way through a selection can lose its selection for good, and nothing on the binding side then puts it back. Rather than rely on never hitting that,
    // whenever the answer changes the window checks — once things have settled — that each box really is showing it, and if not sets the selection directly. SetCurrentValue keeps the
    // binding; SuppressStyleWrites stops the view model mistaking the correction for a choice.

    private bool _styleSyncQueued;

    private void QueueStyleComboSync()
    {
        if (_styleSyncQueued) return;
        _styleSyncQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _styleSyncQueued = false;
            SyncStyleCombos();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void SyncStyleCombos()
    {
        ViewModel.SuppressStyleWrites = true;
        try
        {
            Reassert(StrokeColorCombo, ViewModel.CurrentPolygonColorOption);
            Reassert(FillColorCombo, ViewModel.CurrentPolygonFillColorOption);
        }
        finally
        {
            ViewModel.SuppressStyleWrites = false;
        }

        static void Reassert(System.Windows.Controls.ComboBox box, object? wanted)
        {
            if (wanted is null || ReferenceEquals(box.SelectedItem, wanted) || !box.Items.Contains(wanted)) return;
            box.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, wanted);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPolygonColorOption) or nameof(MainViewModel.CurrentPolygonFillColorOption)
            or nameof(MainViewModel.AvailableStrokeColorOptions) or nameof(MainViewModel.AvailableFillColorOptions))
            QueueStyleComboSync();

        // The in-place text editor is sized and placed for the current zoom and page, so a zoom change keeps the
        // text typed so far (and the editor closes), while moving to another page/job abandons it.
        if (e.PropertyName == nameof(MainViewModel.Zoom))
            EndInlineTextEdit(commit: true);
        else if (e.PropertyName is nameof(MainViewModel.CurrentPageIndex) or nameof(MainViewModel.CurrentJobId))
            EndInlineTextEdit(commit: false);

        if (e.PropertyName is nameof(MainViewModel.PreviewImage) or nameof(MainViewModel.Zoom))
        {
            // Dispatched at Loaded priority so layout has actually re-measured/arranged before we
            // query TransformToVisual — querying synchronously right after the property change
            // would read stale (pre-update) positions.
            Dispatcher.BeginInvoke(new Action(UpdateFrozenOverlayPosition), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (e.PropertyName is nameof(MainViewModel.IsSelectMode) or nameof(MainViewModel.IsMoveMode))
        {
            ApplyModeCursor();
        }
        else if (e.PropertyName == nameof(MainViewModel.DialogScale))
        {
            ScaleDockedPanelWidth(); // the docked Effects panel is a "panel", so it follows the dialog scale, not the toolbar scale
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentJobId))
        {
            // Catches every way a job can get loaded — the Previous/Next/Open Job buttons here,
            // Open Image(s), and the Watcher's own automatic print-job notifications via
            // App.xaml.cs — rather than only updating after the three button handlers in this file.
            UpdateJobNavigationButtonStates();
        }
    }

    /// <summary>The status bar's job-cache warning doubles as a shortcut to where the cache is
    /// actually managed — clicking it opens the same Open Job dialog.</summary>
    private void JobCacheWarning_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OpenJobButton_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>Opens the job browser dialog (list/reopen/delete previously captured or manually-
    /// loaded jobs). Loading the selected job reuses the exact same LoadJob path the Watcher's
    /// automatic print-job notifications already use.</summary>
    private void OpenJobButton_Click(object sender, RoutedEventArgs e)
    {
        var browser = new JobBrowserWindow(ViewModel) { Owner = this };
        if (browser.ShowDialog() == true && browser.SelectedJobFolder is not null)
            ViewModel.LoadJob(browser.SelectedJobFolder);
    }

    /// <summary>Shared by the Previous/Next Job buttons — finds every job folder under
    /// CleanPrintPaths.JobsRoot, sorted the same way JobBrowserWindow lists them (newest first by
    /// last-modified time), locates the currently-loaded job within that list, and loads whichever
    /// one sits at +/- 1 from it. "Previous" moves toward the start of that list (newer); "Next"
    /// moves toward the end (older) — arbitrary but consistent, matching how you'd browse the same
    /// list top-to-bottom in JobBrowserWindow. If nothing is currently loaded, or the loaded job
    /// isn't found (e.g. it was deleted), starts from whichever end "direction" would naturally
    /// enter from, rather than doing nothing.</summary>
    /// <summary>Every job folder under CleanPrintPaths.JobsRoot, sorted the same way
    /// JobBrowserWindow lists them (newest first by last-modified time) — shared by
    /// LoadAdjacentJob and UpdateJobNavigationButtonStates so they can't drift out of sync with
    /// each other about what "previous"/"next" means.</summary>
    private static List<string> GetSortedJobFolders()
    {
        if (!Directory.Exists(CleanPrintPaths.JobsRoot)) return new List<string>();
        return Directory.GetDirectories(CleanPrintPaths.JobsRoot)
            .OrderByDescending(Directory.GetLastWriteTime)
            .ToList();
    }

    /// <summary>Shared by the Previous/Next Job buttons — finds every job folder under
    /// CleanPrintPaths.JobsRoot, sorted the same way JobBrowserWindow lists them (newest first by
    /// last-modified time), locates the currently-loaded job within that list, and loads whichever
    /// one sits at +/- 1 from it. "Previous" moves toward the start of that list (newer); "Next"
    /// moves toward the end (older) — arbitrary but consistent, matching how you'd browse the same
    /// list top-to-bottom in JobBrowserWindow. If nothing is currently loaded, or the loaded job
    /// isn't found (e.g. it was deleted), starts from whichever end "direction" would naturally
    /// enter from, rather than doing nothing.</summary>
    private void LoadAdjacentJob(int direction)
    {
        var jobFolders = GetSortedJobFolders();
        if (jobFolders.Count == 0) return;

        int currentIndex = jobFolders.FindIndex(f =>
            string.Equals(Path.GetFileName(f), ViewModel.CurrentJobId, StringComparison.OrdinalIgnoreCase));

        int newIndex = currentIndex < 0
            ? (direction > 0 ? 0 : jobFolders.Count - 1)
            : currentIndex + direction;

        if (newIndex < 0 || newIndex >= jobFolders.Count) return; // already at the boundary

        ViewModel.LoadJob(jobFolders[newIndex]);
    }

    /// <summary>Enables/disables the Previous/Next Job buttons based on whether there's actually
    /// somewhere to go from the currently-loaded job, within the same sorted list LoadAdjacentJob
    /// uses — called on window Loaded and whenever MainViewModel.CurrentJobId changes (see
    /// ViewModel_PropertyChanged), which covers every way a job can get loaded, not just this
    /// window's own three buttons.</summary>
    private void UpdateJobNavigationButtonStates()
    {
        var jobFolders = GetSortedJobFolders();
        if (jobFolders.Count == 0)
        {
            PreviousJobButton.IsEnabled = false;
            NextJobButton.IsEnabled = false;
            return;
        }

        int currentIndex = jobFolders.FindIndex(f =>
            string.Equals(Path.GetFileName(f), ViewModel.CurrentJobId, StringComparison.OrdinalIgnoreCase));

        if (currentIndex < 0)
        {
            // Nothing currently loaded, or it's not in the list (e.g. deleted) — both stay
            // enabled, since LoadAdjacentJob falls back to loading the first/last job either way.
            PreviousJobButton.IsEnabled = true;
            NextJobButton.IsEnabled = true;
            return;
        }

        PreviousJobButton.IsEnabled = currentIndex > 0;
        NextJobButton.IsEnabled = currentIndex < jobFolders.Count - 1;
    }

    private void PreviousJobButton_Click(object sender, RoutedEventArgs e) => LoadAdjacentJob(-1);

    private void NextJobButton_Click(object sender, RoutedEventArgs e) => LoadAdjacentJob(1);

    /// <summary>
    /// Lets the person load one or more arbitrary image files from disk and run them through the
    /// exact same pipeline (whiten/crop/annotate/split/print) as a captured print job — by
    /// creating a new job folder and re-encoding each selected image as page_NNNN.png there, then
    /// calling LoadJob exactly as if the Watcher had captured it. Re-encoding to real PNG (rather
    /// than just copying/renaming with a .png extension) matters because BitmapDecoder.Create
    /// auto-detects format from content, not extension, but keeping the actual on-disk bytes
    /// genuinely PNG avoids any ambiguity for anything else that later reads these files directly.
    ///
    /// For a single selected image, checks first whether a job already exists for that exact file
    /// (JobSourceService) and offers to reopen it instead of creating a duplicate — skipped for
    /// multiple images at once, since a multi-image job is a different combination each time and
    /// rarely has an exact prior match worth detecting.
    /// </summary>
    /// <summary>The resize cursor for a picture's handle: the double arrow along that handle's direction, turned by the picture's own rotation (so a
    /// handle that now points diagonally gets a diagonal arrow), snapped to the nearest of the four the system offers.</summary>
    private static Cursor CursorForHandle(ImageHandle handle, double rotationDegrees)
    {
        double direction = handle switch
        {
            ImageHandle.Right => 0, ImageHandle.BottomRight => 45, ImageHandle.Bottom => 90, ImageHandle.BottomLeft => 135,
            ImageHandle.Left => 180, ImageHandle.TopLeft => 225, ImageHandle.Top => 270, _ => 315,   // TopRight
        };
        double angle = ((direction + rotationDegrees) % 180 + 180) % 180;
        return ((int)Math.Round(angle / 45.0) % 4) switch
        {
            0 => Cursors.SizeWE,
            1 => Cursors.SizeNWSE,
            2 => Cursors.SizeNS,
            _ => Cursors.SizeNESW,
        };
    }

    /// <summary>Insert Picture: choose an image file; it is copied into the job and placed on the current page, selected.</summary>
    private async void InsertPictureButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Insert Picture",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        await ViewModel.InsertImageFileAsync(dialog.FileName);
    }

    private void ResetImageProportionsButton_Click(object sender, RoutedEventArgs e) => ViewModel.ResetImageProportions();

    /// <summary>New Blank Job: asks for the paper size, orientation, resolution and page count, writes the white pages to a new job folder, and opens it.</summary>
    private async void NewBlankJobButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewBlankJobDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        string? jobFolder = null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // Off the UI thread: encoding many large pages takes a moment.
            bool makePrivate = ViewModel.IsPrivateMode;
            jobFolder = await Task.Run(() => BlankJobService.CreateJob(
                dialog.PaperName, dialog.Landscape, dialog.WidthPx, dialog.HeightPx, dialog.Dpi, dialog.PageCount, makePrivate));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create the blank job: {ex.Message}", "New Blank Job Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (jobFolder is not null)
            ViewModel.LoadJob(jobFolder);
    }

    /// <summary>The page menu (shown beside the page navigation for a blank job): add a page at the end, insert one after the current page, or delete the current page.</summary>
    private void PageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = PageMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        bool idle = !ViewModel.IsBusy;
        AddMenuItem(menu, "Add Page at End", () => _ = ViewModel.AddBlankPageAsync(afterCurrent: false), idle);
        AddMenuItem(menu, "Insert Page After This One", () => _ = ViewModel.AddBlankPageAsync(afterCurrent: true), idle);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Delete This Page…", DeleteCurrentPageWithConfirmation, idle && ViewModel.CanDeletePage);
        menu.IsOpen = true;
    }

    private async void DeleteCurrentPageWithConfirmation()
    {
        var answer = MessageBox.Show(
            $"Delete page {ViewModel.CurrentPageNumber} of {ViewModel.PageCount}?\n\nThe page and everything drawn on it will be removed. This cannot be undone.",
            "Delete Page", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await ViewModel.DeleteCurrentPageAsync();
    }

    private void OpenImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Image(s)",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        OpenImageFiles(dialog.FileNames);
    }

    // ---- Drag and drop: drop image files anywhere on the window to open them ----

    private static readonly string[] DroppableImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    /// <summary>Explorer's own filename ordering (so scan2 comes before scan10), which is the order a person
    /// expects a multi-page drop to come out in.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string left, string right);

    /// <summary>The image files in a drag payload, in natural filename order. Anything else dropped with them
    /// (documents, folders) is ignored rather than failing the whole drop.</summary>
    private static string[] GetDroppedImageFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
            return Array.Empty<string>();

        var images = paths
            .Where(path => File.Exists(path) && DroppableImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .ToList();
        images.Sort(StrCmpLogicalW);
        return images.ToArray();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        // Only advertise "you can drop here" for something Open Image(s) can actually open, and not while a
        // job is busy loading (opening another would race it).
        e.Effects = !ViewModel.IsBusy && GetDroppedImageFiles(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel.IsBusy) return;

        var files = GetDroppedImageFiles(e.Data);
        if (files.Length == 0)
        {
            MessageBox.Show(this, "None of the dropped items is an image file Clean Print can open (PNG, JPG, BMP, TIFF or GIF).",
                "Open Image(s)", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Bring the window forward first: a drop from another app leaves Clean Print behind it, and the
        // duplicate-job prompts that may follow should appear on top of it.
        Activate();
        OpenImageFiles(files);
    }

    /// <summary>Opens image files as a new job (or reopens/updates an existing one if the same image was opened
    /// before) — shared by the Open Image(s) button and drag-and-drop, so both get the same duplicate detection
    /// and "this image changed" handling.</summary>
    private void OpenImageFiles(string[] fileNames)
    {
        // Not in private mode: finding and reopening a job already linked to this image would put the user's work back into the job cache — a private session always starts a fresh private job.
        if (fileNames.Length == 1 && !ViewModel.IsPrivateMode)
        {
            string imagePath = fileNames[0];

            // Covers both "this file was used to create a job" (Open Image(s)) and "this file is
            // an export FROM a job" (when Settings > General's link-exports option is on) — both
            // are recorded into the same source-images.json inside the job's own folder (see
            // JobSourceService's own doc comment), so one lookup and one prompt handles either case
            // identically.
            string? existingJobFolder = JobSourceService.FindJobForImage(imagePath);
            if (existingJobFolder is not null)
            {
                // Offering "update the existing job in place" only makes unambiguous sense for a
                // single-page job — a multi-page job's pages could include original sources AND
                // export-linked paths in the same list, so there's no reliable way to know WHICH
                // page_*.png corresponds to this one path without risking overwriting the wrong
                // page. For that case (and whenever nothing's actually changed), this falls back to
                // the existing open/create-new prompt unchanged.
                bool isSinglePageJob = CleanPrintPaths.GetJobPageFiles(existingJobFolder).Count == 1;
                bool hasChanged = isSinglePageJob && JobSourceService.HasSourceImageChanged(existingJobFolder, imagePath);

                if (hasChanged)
                {
                    var updateChoice = MessageBox.Show(
                        $"This image is linked to an existing CleanPrint job, but the file has changed since it was linked.\n\n" +
                        "Update the existing job with the new image content (Yes), or create a new, separate job from it (No)?",
                        "Linked Image Changed", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (updateChoice == MessageBoxResult.Cancel) return;
                    if (updateChoice == MessageBoxResult.Yes)
                    {
                        try
                        {
                            UpdateJobFromChangedImage(existingJobFolder, imagePath);
                            ViewModel.LoadJob(existingJobFolder);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Could not update the job: {ex.Message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        return;
                    }
                    // No -> fall through and create a new, separate job from the current content
                }
                else
                {
                    var choice = MessageBox.Show(
                        $"This image is already linked to an existing CleanPrint job (\"{Path.GetFileName(imagePath)}\").\n\nWould you like to open the linked job (Yes — keeps its raw capture, polygons, and effects editable) or create a new one from this image (No)?",
                        "Job Already Exists", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Cancel) return;
                    if (choice == MessageBoxResult.Yes)
                    {
                        ViewModel.LoadJob(existingJobFolder);
                        return;
                    }
                    // No -> fall through and create a new job as usual
                }
            }
        }

        try
        {
            // Named after the first selected image (sanitized, since it becomes a folder name),
            // for easier identification later — falls back to the old timestamp-only scheme if
            // that name turns out empty after sanitizing (e.g. a file named just "....png").
            string baseName = SanitizeForFolderName(Path.GetFileNameWithoutExtension(fileNames[0]));
            string jobId = string.IsNullOrEmpty(baseName)
                ? $"manual-{DateTime.Now:yyyyMMdd-HHmmss}"
                : $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}";
            string jobFolder = CleanPrintPaths.NewJobFolder(jobId, ViewModel.IsPrivateMode);   // the job cache, or the private area in private mode

            for (int i = 0; i < fileNames.Length; i++)
            {
                string destPath = Path.Combine(jobFolder, $"page_{i + 1:D4}.png");
                using var sourceStream = File.OpenRead(fileNames[i]);
                var decoder = BitmapDecoder.Create(sourceStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
                using var destStream = File.Create(destPath);
                encoder.Save(destStream);

                JobSourceService.RecordSourcePath(jobFolder, fileNames[i]);
            }

            ViewModel.LoadJob(jobFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load the selected image(s): {ex.Message}", "Open Image Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Re-encodes imagePath's CURRENT content over the existing job's single page_0001.png
    /// (only ever called for a confirmed single-page job — see OpenImageButton_Click), then refreshes
    /// the recorded timestamp so the job isn't flagged as "changed" again until the file genuinely
    /// changes once more. Deliberately leaves polygon annotations and effect settings untouched:
    /// the new content still goes through the job's existing effect settings on load, and any
    /// polygons that no longer line up with the new content are left for the person to notice and
    /// adjust themselves, rather than this guessing which (if any) are still valid.</summary>
    private static void UpdateJobFromChangedImage(string jobFolder, string imagePath)
    {
        string destPath = Path.Combine(jobFolder, "page_0001.png");
        using (var sourceStream = File.OpenRead(imagePath))
        {
            var decoder = BitmapDecoder.Create(sourceStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var destStream = File.Create(destPath);
            encoder.Save(destStream);
        }

        JobSourceService.RecordSourcePath(jobFolder, imagePath);
    }

    /// <summary>Strips characters that aren't valid in a Windows folder name and caps length —
    /// used to turn a source image's own filename into a job folder name, so job folder + page
    /// file paths underneath it don't risk running unreasonably long.</summary>
    private static string SanitizeForFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 60 ? name[..60] : name;
    }

    /// <summary>Sets Zoom so the page's full width exactly fills the scrollable viewport (minus the
    /// page's own 20px padding on each side — see the padded/centered Border around the page
    /// image), falling back to ActualWidth if the ScrollViewer hasn't reported a viewport yet.
    /// Clamped to the zoom slider's own [0.1, 4] range so the result is always representable there.</summary>
    // ---- Custom title bar / window chrome (see the WindowChrome block in MainWindow.xaml) ----

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateWindowStateVisuals();

    /// <summary>Everything that depends on whether the window is maximized.
    /// (1) Swaps the maximize/restore icon.
    /// (2) A maximized WindowChrome window is positioned with its invisible resize border hanging off
    /// every edge of the screen, which would push the title bar's top and the status bar's bottom
    /// out of view. Padding the window's own border by exactly that thickness while maximized pulls the
    /// content back inside the visible work area (the border itself is then the off-screen part). When
    /// not maximized it's just a 1px outline, since a borderless window has no frame to define its edge.</summary>
    private void UpdateWindowStateVisuals()
    {
        bool maximized = WindowState == WindowState.Maximized;
        BorderThickness = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(1);
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The OS treats the top CaptionHeight pixels of the client area as the draggable title bar,
    /// so that height has to follow the bar's real height — which isn't fixed: the Toolbar scale setting
    /// grows and shrinks the button groups on it. Interactive children opt back out of dragging via
    /// WindowChrome.IsHitTestVisibleInChrome (set in their button style).</summary>
    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null && e.NewSize.Height > 0)
            chrome.CaptionHeight = e.NewSize.Height;
    }

    // ---- Page box (status bar) ----

    /// <summary>The page box commits when it loses focus or on Enter — NOT on every keystroke, since each
    /// commit loads a page and typing "12" shouldn't load page 1 on the way. After Enter, keyboard focus
    /// is released so the single-key shortcuts (W, X, P, ...) work again straight away; they're ignored
    /// while a text box has focus.</summary>
    private void PageNumberTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        System.Windows.Data.BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ---- Zoom buttons (status bar) — same step as the keyboard shortcuts and mouse wheel ----

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ViewModel.Zoom *= ViewModel.ZoomWheelSensitivity;

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ViewModel.Zoom /= ViewModel.ZoomWheelSensitivity;

    private void FitWidthButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PreviewImage is null || ViewModel.PreviewImage.Width <= 0) return;

        double available = PreviewScrollViewer.ViewportWidth > 0 ? PreviewScrollViewer.ViewportWidth : PreviewScrollViewer.ActualWidth;
        if (available <= 0) return;

        double fitZoom = (available - 40) / ViewModel.PreviewImage.Width; // 40 = 20px padding x 2 sides
        ViewModel.Zoom = Math.Clamp(fitZoom, 0.1, 4.0);
    }

    /// <summary>Same as FitWidthButton_Click, but fits the page's full height instead.</summary>
    private void FitHeightButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PreviewImage is null || ViewModel.PreviewImage.Height <= 0) return;

        double available = PreviewScrollViewer.ViewportHeight > 0 ? PreviewScrollViewer.ViewportHeight : PreviewScrollViewer.ActualHeight;
        if (available <= 0) return;

        double fitZoom = (available - 40) / ViewModel.PreviewImage.Height;
        ViewModel.Zoom = Math.Clamp(fitZoom, 0.1, 4.0);
    }

    private async void RotateClockwiseButton_Click(object sender, RoutedEventArgs e) => await ConfirmAndRotateAsync(clockwise: true);

    private async void RotateCounterclockwiseButton_Click(object sender, RoutedEventArgs e) => await ConfirmAndRotateAsync(clockwise: false);

    /// <summary>Warns before rotating only if the current page actually has polygons that would be
    /// lost (checking the currently-shown variant's live Polygons collection as a low-risk proxy —
    /// rotation clears both Before/After variants' saved polygons for this page, but there's no
    /// point prompting if there's visibly nothing here to lose).</summary>
    private async Task ConfirmAndRotateAsync(bool clockwise)
    {
        if (ViewModel.Polygons.Count > 0)
        {
            var confirm = MessageBox.Show(
                "Rotating this page will clear any polygons drawn on it (Freeze Row/Column, Hide Row/Column, fills, etc.), since their positions would no longer line up with the rotated image.\n\nContinue?",
                "Rotate Page", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        await ViewModel.RotatePageAsync(clockwise);
    }

    private EffectsPanel? _effectsPanel;
    private Window? _effectsFloatingWindow;
    private bool _effectsDocked;

    private const double EffectsDockWidth = 340;
    private const double EffectsDockSplitterWidth = EffectsPanel.DockedSplitterStripWidth; // the XAML's GridSplitter Width must match

    /// <summary>Toggles the Effects panel's visibility. First click creates it (docked by default,
    /// per the request that it should genuinely merge with the preview area). While docked, this
    /// button shows/hides it within MainWindow's own layout — since effects are non-destructive and
    /// always applied automatically regardless of whether the panel is visible, "closing" it just
    /// means hiding the controls, not undoing anything. While floating, this brings the floating
    /// window forward instead of fighting with an explicitly undocked window — that window's own
    /// title bar already provides a way to close/hide it.</summary>
    private void EffectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_effectsPanel is null)
        {
            _effectsPanel = new EffectsPanel(ViewModel.EffectSettings);
            _effectsPanel.DockToggleRequested += (_, _) => ToggleEffectsDock();
            _effectsDocked = true;
            _effectsPanel.SetDockedState(true);
            ShowDockedPanel();
            return;
        }

        if (!_effectsDocked)
        {
            _effectsFloatingWindow?.Show();
            _effectsFloatingWindow?.Activate();
            return;
        }

        if (EffectsDockColumn.Width.Value > 0)
            HideDockedPanel();
        else
            ShowDockedPanel();
    }

    private void ShowDockedPanel()
    {
        EffectsDockHost.Content = _effectsPanel;
        _dockScale = UiScaleSource.Instance.Scale;
        EffectsDockColumn.Width = new GridLength(EffectsDockWidth * _dockScale);
        EffectsDockSplitterColumn.Width = new GridLength(EffectsDockSplitterWidth);
        RefreshFrozenOverlayAfterLayoutChange();
    }

    /// <summary>The Dialog &amp; panel scale the docked Effects column width currently reflects. The panel scales its
    /// content (UiScaling Live), and a LayoutTransform inside a fixed-width column doesn't make the
    /// column bigger — it makes the same width hold LESS layout, cramping the sliders. So the column
    /// follows the scale too, keeping whatever width the person had dragged it to, proportionally.</summary>
    private double _dockScale = 1.0;

    private void ScaleDockedPanelWidth()
    {
        double now = UiScaleSource.Instance.Scale;
        if (EffectsDockColumn.Width.Value > 0 && _dockScale > 0)
        {
            EffectsDockColumn.Width = new GridLength(EffectsDockColumn.Width.Value * now / _dockScale);
            RefreshFrozenOverlayAfterLayoutChange();
        }
        _dockScale = now;
    }

    private void HideDockedPanel()
    {
        EffectsDockHost.Content = null;
        EffectsDockColumn.Width = new GridLength(0);
        EffectsDockSplitterColumn.Width = new GridLength(0);
        RefreshFrozenOverlayAfterLayoutChange();
    }

    /// <summary>Undocks if currently docked. If currently floating, closes the floating window
    /// instead of re-docking directly — EffectsFloatingWindow_Closing below does the actual
    /// re-docking work, so that path and "the user closes the floating window via its own [X]
    /// button" converge on exactly the same logic rather than needing to be kept in sync separately.</summary>
    private void ToggleEffectsDock()
    {
        if (_effectsDocked)
        {
            if (_effectsPanel is null) return;
            EffectsDockHost.Content = null;
            EffectsDockColumn.Width = new GridLength(0);
            EffectsDockSplitterColumn.Width = new GridLength(0);
            _effectsDocked = false;
            _effectsPanel.SetDockedState(false);

            _effectsFloatingWindow = new Window
            {
                Title = "Effects",
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/CleanPrint.ico", UriKind.Absolute)),
                Width = 420,
                Height = 620,
                Owner = this,
                Content = _effectsPanel,
            };
            UiScaling.SetMode(_effectsFloatingWindow, UiScaleMode.SizeOnly); // the panel inside already scales itself; the window just needs to grow/shrink to match
            _effectsFloatingWindow.Closing += EffectsFloatingWindow_Closing;
            _effectsFloatingWindow.Show();
            RefreshFrozenOverlayAfterLayoutChange();
        }
        else
        {
            _effectsFloatingWindow?.Close();
        }
    }

    /// <summary>Closing the floating window (via its own [X], or via the panel's "Dock to Main
    /// Window" button closing it through ToggleEffectsDock) re-docks the panel rather than losing
    /// it — the panel itself is never destroyed, just moved back to its home in the main window.</summary>
    private void EffectsFloatingWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_effectsPanel is null || _effectsFloatingWindow is null) return;

        _effectsFloatingWindow.Content = null; // detach the panel before the window actually closes
        _effectsFloatingWindow.Closing -= EffectsFloatingWindow_Closing;
        _effectsFloatingWindow = null;

        _effectsDocked = true;
        _effectsPanel.SetDockedState(true);
        ShowDockedPanel();
    }

    /// <summary>Docking/undocking (or showing/hiding the docked panel) changes EffectsDockColumn's
    /// width, which shifts the preview area's actual screen X position — the Freeze Row overlay's
    /// TransformToVisual-based positioning depends on that, so it needs refreshing the same way
    /// PreviewImage/Zoom changes already trigger it (dispatched at Loaded priority so layout has
    /// actually re-arranged first).</summary>
    private void RefreshFrozenOverlayAfterLayoutChange() =>
        Dispatcher.BeginInvoke(new Action(UpdateFrozenOverlayPosition), System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>Opens Settings (modal — unlike Effects, this isn't something you'd adjust while
    /// watching a live preview, so there's no benefit to keeping the main window interactive at
    /// the same time). A fresh dialog each time rather than a tracked singleton, since it's cheap
    /// to construct and re-reads the current preset list from disk on each open anyway.</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel) { Owner = this };
        dialog.ShowDialog();
    }

    private PrintDialog? _printDialog;

    /// <summary>Opens the Split &amp; Print dialog (ver_0.3.0: moved out of MainWindow's own
    /// layout, which now stays fully dedicated to the image preview). Non-modal — Show, not
    /// ShowDialog — so the main window stays interactive (e.g. to check a page before configuring
    /// a per-page override).
    ///
    /// Created ONCE and reused for the entire app session rather than recreated on every open — a
    /// previous version constructed a brand new PrintDialog each time and let the default Closed
    /// handler null out the reference, which crashed on the SECOND open (closing the window's own
    /// [X] tears down its generated component connections in a way that a fresh construction
    /// afterward didn't tolerate). Intercepting Closing and hiding instead of actually closing
    /// sidesteps that entirely: the same Window instance, and everything InitializeComponent wired
    /// up inside it, is never torn down for the rest of the process's lifetime — the cost is one
    /// Window object kept alive in memory, which is negligible.</summary>
    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_printDialog is null)
        {
            _printDialog = new PrintDialog(ViewModel) { Owner = this };
            _printDialog.Closing += (_, args) =>
            {
                args.Cancel = true;
                _printDialog!.Hide();
            };
        }

        // Copies is deliberately reset every time the dialog is opened (not just once per app
        // session, and never persisted to disk — see AppSettingsService's own comment on this) —
        // it's a per-print-job decision, not a preference that should silently carry over from
        // whatever was printed last, in this session or any other.
        ViewModel.Copies = 1;

        _printDialog.Show();
        _printDialog.Activate();
    }

    /// <summary>Exports just the currently-displayed page (not the whole job, not split tiles) —
    /// see MainViewModel.ExportCurrentPageAsync. Separate from PrintDialog's own Export, which
    /// follows the Print Whole Page/Split Tiles choice and can cover the whole job.</summary>
    /// <summary>The "Turn private mode off" button on the purple strip.</summary>
    private void TurnOffPrivateButton_Click(object sender, RoutedEventArgs e) => ViewModel.IsPrivateMode = false;

    private async void ExportCurrentPageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Current Page",
            Filter = "PNG Image|*.png|JPEG Image|*.jpg",
            FilterIndex = ViewModel.DefaultExportFilterIndex,   // Settings > Capture & Export > Default image format
            FileName = "page",
        };
        if (dialog.ShowDialog(this) != true) return;

        string format = Path.GetExtension(dialog.FileName).TrimStart('.').ToLowerInvariant();

        try
        {
            await ViewModel.ExportCurrentPageAsync(dialog.FileName, format);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Restores the window to wherever it was left last time. On first run (nothing saved yet),
    /// starts maximized instead of at a fixed pixel size — a fixed size can overflow the visible
    /// work area on smaller or differently-configured screens, hiding the title bar above the top
    /// or the bottom edge behind the taskbar. Maximized always fits, on any screen.
    /// </summary>
    private void ApplySavedWindowPlacement()
    {
        var saved = WindowPlacementService.Load();
        if (saved is null)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        // Clamp to the current virtual screen bounds in case the saved position came from a
        // different monitor configuration (e.g. an external display that's no longer connected) —
        // otherwise the window could restore fully or partially off-screen.
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;

        double width = Math.Min(saved.Width, virtualWidth);
        double height = Math.Min(saved.Height, virtualHeight);
        double left = Math.Clamp(saved.Left, virtualLeft, virtualLeft + virtualWidth - width);
        double top = Math.Clamp(saved.Top, virtualTop, virtualTop + virtualHeight - height);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        WindowState = saved.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SaveWindowPlacement()
    {
        // RestoreBounds gives the "normal" (non-maximized) rectangle even while currently
        // maximized, so restoring later reproduces both the maximized state AND the size/position
        // it would return to if un-maximized.
        var bounds = RestoreBounds;
        WindowPlacementService.Save(new WindowPlacementSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        });
    }

    // ---- Win32 interop for reliably stealing focus from a background process (see BringToFront) ----

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Called when the Watcher (or a second launched instance) hands off a newly-captured job to
    /// this already-running App — the window needs to come not just visually to the front, but
    /// genuinely take KEYBOARD FOCUS, since without it, shortcuts like W (fit width) or X (export)
    /// silently do nothing: the OS is still routing keyboard input to whatever window WAS in the
    /// foreground when the Watcher fired, not to us.
    ///
    /// The plain Show/Activate/Topmost-toggle sequence this used to do is a well-known WPF
    /// workaround, but it only reliably changes Z-order (visually on top) — Windows' focus-stealing
    /// prevention can still block the actual keyboard-input handoff, since Topmost and foreground/
    /// input-focus are different things. AttachThreadInput below is the standard, more reliable
    /// technique for a background process to take real input focus: it temporarily merges this
    /// thread's input state with whatever thread currently owns the foreground window, which is
    /// what actually lets SetForegroundWindow succeed from a caller Windows would otherwise refuse.
    /// </summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();

        var hWnd = new WindowInteropHelper(this).Handle;
        var currentForeground = GetForegroundWindow();
        uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
        uint thisThreadId = GetCurrentThreadId();

        if (foregroundThreadId != thisThreadId)
        {
            AttachThreadInput(thisThreadId, foregroundThreadId, true);
            try
            {
                // ShowWindow(hWnd, SW_RESTORE) used to be here — SW_RESTORE unconditionally forces
                // a window into its RESTORED (non-maximized) state, which was silently un-maximizing
                // this window on every single auto-open regardless of whether it was maximized
                // beforehand. Show() above already makes the window visible, and the WindowState
                // check further up already un-minimizes if needed — BringWindowToTop/
                // SetForegroundWindow below only affect Z-order and input focus, never size/state,
                // so dropping ShowWindow entirely fixes the regression without losing the actual
                // focus-stealing fix.
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                AttachThreadInput(thisThreadId, foregroundThreadId, false);
            }
        }
        else
        {
            SetForegroundWindow(hWnd);
        }

        Activate();
        Focus();
    }

    // ---- Preview interaction: pan with left-drag, wheel scroll/zoom ----

    private Point? _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    /// <summary>
    /// Plain wheel = default vertical scroll (left unhandled, so ScrollViewer's built-in behavior
    /// applies). Shift+wheel = horizontal scroll. Ctrl+wheel = zoom, via the ViewModel's Zoom
    /// property (already clamped to a sensible range there).
    /// </summary>
    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            double factor = e.Delta > 0 ? ViewModel.ZoomWheelSensitivity : 1 / ViewModel.ZoomWheelSensitivity;
            ViewModel.Zoom *= factor;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            PreviewScrollViewer.ScrollToHorizontalOffset(PreviewScrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
        // No modifier: leave e.Handled = false so the ScrollViewer's own vertical wheel scrolling runs normally.
    }

    private void PreviewScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // PreviewMouseLeftButtonDown tunnels through the ScrollViewer on its way to whatever was
        // actually clicked, including its own scrollbar thumbs — without this check, starting a
        // pan here would hijack clicks on the real scrollbars and break normal dragging of them.
        if (IsWithinScrollBar(e.OriginalSource as DependencyObject))
            return;

        // The in-place text editor: a click inside it is the editor's own; a click anywhere else commits it,
        // and then carries on as an ordinary click (e.g. selecting something else).
        if (_inlineTextBox is not null)
        {
            if (_inlineTextBox.IsMouseOver) return;
            EndInlineTextEdit(commit: true);
        }

        ReleaseToolbarFocus();

        if (ViewModel.IsDrawingPolygon)
            return; // mid-drawing a brand-new polygon: FreezeEditCanvas handles this exclusively

        // The Move command: a drag picks up whatever annotation is under the pointer — by its edge, with no need to select it
        // first (and if it belongs to the current selection, the whole selection moves with it). Dragging empty space while
        // something is selected moves the selection too.
        if (ViewModel.IsMoveMode)
        {
            var movePos = GetImageSpacePosition(e);
            var grabbed = ViewModel.FindPolygonNearPoint(movePos.X, movePos.Y, FreezeVertexHitTolerance);
            if (grabbed is not null || ViewModel.Polygons.Any(p => p.IsSelected))
            {
                if (grabbed is not null && !grabbed.IsSelected)
                    ViewModel.SelectPolygon(grabbed, addToSelection: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                _moveLastPoint = movePos;
                PreviewScrollViewer.CaptureMouse();
                PreviewScrollViewer.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
        }

        // Select mode (or Shift held): a drag that starts on empty space draws a selection rectangle. Starting ON an annotation
        // still just selects it, as always.
        if (ViewModel.IsSelectMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var startPos = GetImageSpacePosition(e);
            if (ViewModel.FindPolygonNearPoint(startPos.X, startPos.Y, FreezeVertexHitTolerance) is null)
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    ViewModel.ClearPolygonSelection(); // the old selection goes now, so it is plain what the rectangle is about to replace
                BeginMarquee(e);
                e.Handled = true;
                return;
            }
        }

        // Double-click on a text annotation edits it in place. (Checked before the "near the editable polygon"
        // shortcut below, which would otherwise hand the second click to the vertex-dragging code.)
        if (e.ClickCount == 2)
        {
            var clickPos = GetImageSpacePosition(e);
            var textHit = ViewModel.FindPolygonNearPoint(clickPos.X, clickPos.Y, FreezeVertexHitTolerance);
            if (textHit is { IsText: true })
            {
                ViewModel.SelectPolygon(textHit, addToSelection: false);
                BeginInlineTextEdit(textHit, isNew: false);
                e.Handled = true;
                return;
            }
        }

        var imagePos = GetImageSpacePosition(e);
        bool ctrlHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // If a polygon is already the sole selected/editable one, a click near IT (a vertex or its
        // own line) should continue editing it as before — let FreezeEditCanvas's own handler
        // (hit-test-visible while IsEditingPolygon) deal with add/drag/remove. A click that ISN'T
        // near it instead changes the selection (below), rather than being silently swallowed.
        var editable = ViewModel.EditablePolygon;
        if (editable is not null)
        {
            bool nearEditable =
                ViewModel.FindNearestVertexIndex(imagePos.X, imagePos.Y, FreezeVertexHitTolerance) >= 0 ||
                ViewModel.IsPointNearPolygonLine(editable, imagePos.X, imagePos.Y, FreezeVertexHitTolerance);
            // A selected PICTURE is a solid area, so a click anywhere on it is "near" it — which would make text or a shape lying on top of it impossible to
            // pick (the click would just keep dragging the picture). So, off its handles, whatever is higher in the stacking order at that point wins.
            if (nearEditable && editable.IsImage && ViewModel.HitImageHandle(editable, imagePos.X, imagePos.Y) is null)
            {
                var topmost = ViewModel.FindPolygonNearPoint(imagePos.X, imagePos.Y, FreezeVertexHitTolerance);
                if (topmost is not null && !ReferenceEquals(topmost, editable))
                    nearEditable = false;
            }
            if (nearEditable)
                return;
        }

        // Not near the current editable polygon (or nothing is editable) — check whether this
        // click landed on SOME polygon (editable or not) and select it. Ctrl toggles it into/out
        // of a multi-selection instead of replacing the selection outright.
        var hitPolygon = ViewModel.FindPolygonNearPoint(imagePos.X, imagePos.Y, FreezeVertexHitTolerance);
        if (hitPolygon is not null)
        {
            ViewModel.SelectPolygon(hitPolygon, addToSelection: ctrlHeld);
            e.Handled = true;
            return;
        }

        // Clicked empty space: clear the selection (unless Ctrl is held, in which case leave it
        // alone) and fall through to normal panning.
        if (!ctrlHeld)
            ViewModel.ClearPolygonSelection();

        BeginPan(e.GetPosition(PreviewScrollViewer), middleButton: false);
    }

    /// <summary>
    /// Right-click: while drawing a new polygon, offers Close/Finish/Cancel. Otherwise, if the
    /// click landed on an existing polygon, selects it (if not already part of the current
    /// selection) and offers purpose assignment (Freeze Row/Column, Unfreeze, Hide Area — recorded
    /// now, not yet acted on), style changes are via the toolbar combo boxes, and Delete. Built
    /// programmatically since the menu's content depends on this dynamic state.
    /// </summary>
    private void PreviewScrollViewer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ReleaseToolbarFocus();
        var imagePos = GetImageSpacePosition(e);
        var menu = new ContextMenu();

        if (ViewModel.IsDrawingShape)
        {
            AddMenuItem(menu, "Cancel", () => ViewModel.CancelPolygonEditCommand.Execute(null));
        }
        else if (ViewModel.IsDrawingFreeformPolygon)
        {
            AddMenuItem(menu, "Close Polygon", () =>
            {
                if (ViewModel.ClosePolygonCommand.CanExecute(null))
                    ViewModel.ClosePolygonCommand.Execute(null);
            }, ViewModel.CanCloseCurrentPolygon);
            AddMenuItem(menu, "Finish (leave open)", () => ViewModel.FinishPolygonCommand.Execute(null));
            AddMenuItem(menu, "Cancel", () => ViewModel.CancelPolygonEditCommand.Execute(null));
        }
        else
        {
            var hit = ViewModel.FindPolygonNearPoint(imagePos.X, imagePos.Y, FreezeVertexHitTolerance);
            if (hit is not null)
            {
                menu = BuildPurposeMenuForPolygon(hit);
            }
            else
            {
                if (ViewModel.Polygons.Any(p => p.IsSelected))
                {
                    AddMenuItem(menu, "Move Selected", () => ViewModel.IsMoveMode = true);
                    menu.Items.Add(new Separator());
                }
                AddMenuItem(menu, "New Polygon", () => ViewModel.NewPolygonCommand.Execute(null));
                AddMenuItem(menu, "New Rectangle", () => ViewModel.NewRectangleCommand.Execute(null));
                AddMenuItem(menu, "New Ellipse", () => ViewModel.NewEllipseCommand.Execute(null));
                AddMenuItem(menu, "New Circle (Centre and Radius)", () => ViewModel.NewCircleCenterCommand.Execute(null));
                AddMenuItem(menu, "New Circle (3 Points)", () => ViewModel.NewCircle3Command.Execute(null));
                AddMenuItem(menu, "New Arc (3 Points)", () => ViewModel.NewArcCommand.Execute(null));
                AddMenuItem(menu, "New Curve", () => ViewModel.NewCurveCommand.Execute(null));
                AddMenuItem(menu, "New Text", () => ViewModel.NewTextCommand.Execute(null));
            }
        }

        // The page itself: on every right-click menu except the ones that are only about finishing a drawing.
        if (!ViewModel.IsDrawingShape && !ViewModel.IsDrawingFreeformPolygon)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AddMenuItem(menu, "Edit Base Image…", EditBaseImage, !ViewModel.IsBusy);
        }

        if (menu.Items.Count > 0)
        {
            PreviewScrollViewer.ContextMenu = menu;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    /// <summary>Edit Base Image: opens the editor on the current page's image (turn by an angle, crop, effects); if it is saved, the result replaces the page's image for good and
    /// everything drawn on the page is moved to follow it.</summary>
    private async void EditBaseImage()
    {
        var context = ViewModel.GetBaseImageEditContext();
        if (context is null) return;

        var editor = new BaseImageEditorWindow(context) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result)
            await ViewModel.ApplyBaseImageEditAsync(result);
    }

    /// <summary>
    /// Builds the purpose-assignment context menu (Freeze Row/Column, Hide Row/Column, Unfreeze/
    /// Unhide, Delete) for a SPECIFIC, already-known polygon — shared by the normal position-based
    /// right-click handler and the frozen overlay's handler (which already knows exactly which
    /// polygon was clicked via its DataContext, with no need to hit-test by position).
    /// </summary>
    private ContextMenu BuildPurposeMenuForPolygon(PolygonViewModel polygon)
    {
        ViewModel.EnsureSelectedForContextMenu(polygon);

        var menu = new ContextMenu();
        AddMenuItem(menu, "Move", () => ViewModel.IsMoveMode = true); // the Move command: then drag this (or anything) by its edge
        if (polygon.Purpose == PolygonPurpose.None) // Freeze/Hide bands can't be mirrored (see MainViewModel.MirrorSelection)
        {
            AddMenuItem(menu, "Mirror Horizontally", () => ViewModel.MirrorHorizontalCommand.Execute(null));
            AddMenuItem(menu, "Mirror Vertically", () => ViewModel.MirrorVerticalCommand.Execute(null));
        }
        menu.Items.Add(new Separator());

        if (polygon.IsText)
        {
            AddMenuItem(menu, "Edit Text", () => BeginInlineTextEdit(polygon, isNew: false));
            menu.Items.Add(new Separator());
        }

        // Only meaningful for an OPEN polyline or curve — a closed one is already fully editable via normal
        // select + drag/insert/delete, and "add more points to extend it" isn't a sensible action
        // on a closed loop. This is the only way to get an already-finished open polyline back into
        // a state where Enter/Close Polygon/Finish work again, since those otherwise only act while
        // a polygon is being freshly drawn (_drawingPolygon), which plain selection doesn't set. Not offered
        // for the fixed-point-count shapes (an arc is open but is defined by exactly three points).
        if (!polygon.IsClosed && !polygon.HasFixedPointCount)
        {
            AddMenuItem(menu, "Edit Polyline (Add Points / Close)", () => ViewModel.BeginEditingExistingOpenPolygon(polygon));
            menu.Items.Add(new Separator());
        }

        // Freeze/Hide read a shape's extent from its vertices — true only for a polygon/rectangle (a circle's
        // control points are not its bounding box), so every other kind is just Delete.
        if (polygon.Kind != ShapeKind.Polygon)
        {
            AddMenuItem(menu, polygon.IsText ? "Delete Text" : "Delete", () => ViewModel.DeleteSelectedPolygonsCommand.Execute(null));
            return menu;
        }

        // If a purpose is already assigned, only offer to clear it (worded to match what it
        // currently is) — re-offering the full set of "assign a NEW purpose" options on a polygon
        // that's already doing one of these jobs is unnecessary clutter, not a real choice anyone
        // would make. If NO purpose is assigned yet, offer every assignment option instead, with no
        // "Clear Purpose" placeholder at all, since there's nothing to clear.
        if (polygon.HasPurpose)
        {
            string clearPurposeLabel = polygon.Purpose switch
            {
                PolygonPurpose.HideRow or PolygonPurpose.HideColumn => "Unhide",
                PolygonPurpose.FreezeRow or PolygonPurpose.FreezeColumn => "Unfreeze",
                _ => "Clear Purpose", // unreachable in practice — HasPurpose is false for None — kept as a safe fallback
            };
            AddMenuItem(menu, clearPurposeLabel, () => ViewModel.SetPurposeForSelection(PolygonPurpose.None));
        }
        else
        {
            AddMenuItem(menu, "Freeze Row", () =>
            {
                ViewModel.SetPurposeForSelection(PolygonPurpose.FreezeRow);
                // Positions the pinned overlay immediately — repositioning otherwise only happens on
                // scroll/zoom/page-change, and there's no such event to piggyback on right when a
                // purpose is first assigned via this menu.
                Dispatcher.BeginInvoke(new Action(UpdateFrozenOverlayPosition), System.Windows.Threading.DispatcherPriority.Loaded);
            });
            AddMenuItem(menu, "Freeze Column", () =>
            {
                ViewModel.SetPurposeForSelection(PolygonPurpose.FreezeColumn);
                Dispatcher.BeginInvoke(new Action(UpdateFrozenOverlayPosition), System.Windows.Threading.DispatcherPriority.Loaded);
            });
            AddMenuItem(menu, "Hide Row", () => ViewModel.SetPurposeForSelection(PolygonPurpose.HideRow));
            AddMenuItem(menu, "Hide Column", () => ViewModel.SetPurposeForSelection(PolygonPurpose.HideColumn));
        }

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Delete Polygon", () => ViewModel.DeleteSelectedPolygonsCommand.Execute(null));
        return menu;
    }

    /// <summary>
    /// Once a polygon is frozen, its normal scrollable-layer outline is hidden
    /// (PolygonViewModel.ShowInScrollableLayer) and the pinned band becomes the only visible — and
    /// now interactive — representation. Left-click selects it, matching how clicking the normal
    /// outline behaves; DataContext already tells us exactly which polygon this is, no position
    /// hit-testing needed.
    /// </summary>
    private void FrozenOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PolygonViewModel polygon }) return;

        bool ctrlHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ViewModel.SelectPolygon(polygon, addToSelection: ctrlHeld);
        e.Handled = true;
    }

    /// <summary>Right-click on the frozen band itself shows the same purpose/delete menu as
    /// right-clicking the polygon normally would — "behaves like the main rectangle" once frozen,
    /// per the request that prompted this.</summary>
    private void FrozenOverlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PolygonViewModel polygon } element) return;

        var menu = BuildPurposeMenuForPolygon(polygon);
        if (menu.Items.Count > 0)
        {
            element.ContextMenu = menu;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static bool IsWithinScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ScrollBar)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- Panning, rectangle selection and moving ----
    // A plain left-button drag on EMPTY space pans the page (the middle button pans too, anywhere). Two toolbar modes change what that
    // drag does: Select (or holding Shift) draws a selection rectangle; Move (also right-click > Move) picks up annotations by their
    // edge, without selecting them first. All of this works from the ScrollViewer's mouse handlers (not the edit canvas), since the
    // canvas only receives the mouse while something is already selected.

    private bool _panWithMiddle;                        // the pan in progress was started with the middle button

    /// <summary>
    /// A click on the page means "I'm working on the page", so keyboard focus leaves any text box in the toolbar (the number boxes, the
    /// page box). It matters because the page shortcuts — Ctrl+Z, Ctrl+C/X/V/A, Delete, Esc — deliberately stand aside while a text box has
    /// focus, so that they still edit its text. But the mouse handlers here handle the click themselves, so WPF never gets to move focus
    /// for us: after using a number box, focus stayed in it, and Ctrl+Z quietly went to that box instead of undoing the page.
    /// (The Undo BUTTON never had the problem, which is why one worked and the other didn't.) The text-editing box is left alone.
    /// </summary>
    private void ReleaseToolbarFocus()
    {
        if (_inlineTextBox is null && Keyboard.FocusedElement is TextBox)
            Keyboard.ClearFocus();
    }

    private void BeginPan(Point startInViewer, bool middleButton)
    {
        _panStartPoint = startInViewer;
        _panStartHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = PreviewScrollViewer.VerticalOffset;
        _panWithMiddle = middleButton;
        PreviewScrollViewer.CaptureMouse(); // keeps receiving move/up events even if the cursor leaves the ScrollViewer mid-drag
        PreviewScrollViewer.Cursor = Cursors.Hand;
    }

    private void EndPan()
    {
        _panStartPoint = null;
        _panWithMiddle = false;
        PreviewScrollViewer.ReleaseMouseCapture();
        ApplyModeCursor();
    }

    /// <summary>Middle button: pan the page.</summary>
    private void PreviewScrollViewer_PreviewMouseDownMiddle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || IsWithinScrollBar(e.OriginalSource as DependencyObject)) return;
        ReleaseToolbarFocus();
        BeginPan(e.GetPosition(PreviewScrollViewer), middleButton: true);
        e.Handled = true;
    }

    private void PreviewScrollViewer_PreviewMouseUpMiddle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_panWithMiddle) return;
        EndPan();
        e.Handled = true;
    }

    private Point? _marqueeStart;                       // in FreezeEditCanvas coordinates
    private System.Windows.Shapes.Rectangle? _marqueeRectangle;
    private Point? _moveLastPoint;                      // in page space

    /// <summary>The cursor the preview should show when nothing else is going on, for the current mode.</summary>
    private void ApplyModeCursor() =>
        PreviewScrollViewer.Cursor = ViewModel.IsSelectMode ? Cursors.Cross : ViewModel.IsMoveMode ? Cursors.SizeAll : Cursors.Arrow;

    private void BeginMarquee(MouseButtonEventArgs e)
    {
        _marqueeStart = e.GetPosition(FreezeEditCanvas);
        _marqueeRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 30, 144, 255)),
            IsHitTestVisible = false,
            Width = 0,
            Height = 0,
        };
        Canvas.SetLeft(_marqueeRectangle, _marqueeStart.Value.X);
        Canvas.SetTop(_marqueeRectangle, _marqueeStart.Value.Y);
        FreezeEditCanvas.Children.Add(_marqueeRectangle);
        PreviewScrollViewer.CaptureMouse();
        PreviewScrollViewer.Cursor = Cursors.Cross;
    }

    private void UpdateMarquee(MouseEventArgs e)
    {
        if (_marqueeStart is not { } start || _marqueeRectangle is null) return;
        var now = e.GetPosition(FreezeEditCanvas);
        Canvas.SetLeft(_marqueeRectangle, Math.Min(start.X, now.X));
        Canvas.SetTop(_marqueeRectangle, Math.Min(start.Y, now.Y));
        _marqueeRectangle.Width = Math.Abs(now.X - start.X);
        _marqueeRectangle.Height = Math.Abs(now.Y - start.Y);
    }

    private void EndMarquee(MouseEventArgs e)
    {
        if (_marqueeStart is not { } start) return;
        var end = e.GetPosition(FreezeEditCanvas);
        if (_marqueeRectangle is not null) FreezeEditCanvas.Children.Remove(_marqueeRectangle);
        _marqueeRectangle = null;
        _marqueeStart = null;

        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (Math.Abs(end.X - start.X) < 3 && Math.Abs(end.Y - start.Y) < 3)
        {
            // Barely moved: that was a click on empty space, which clears the selection (as it always does).
            if (!add) ViewModel.ClearPolygonSelection();
            return;
        }

        var a = CanvasToImageSpace(start);
        var b = CanvasToImageSpace(end);
        int count = ViewModel.SelectInRectangle(a.X, a.Y, b.X, b.Y, add);
        ViewModel.ReportSelection(count);
    }

    private void PreviewScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateMarquee(e);
            return;
        }

        if (_moveLastPoint is { } last && e.LeftButton == MouseButtonState.Pressed)
        {
            var now = GetImageSpacePosition(e);
            ViewModel.MoveSelectedBy(now.X - last.X, now.Y - last.Y);
            _moveLastPoint = now;
            return;
        }

        if (_panStartPoint is null) return;
        bool buttonHeld = _panWithMiddle ? e.MiddleButton == MouseButtonState.Pressed : e.LeftButton == MouseButtonState.Pressed;
        if (!buttonHeld) return;

        var currentPoint = e.GetPosition(PreviewScrollViewer);
        var delta = _panStartPoint.Value - currentPoint;

        PreviewScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset + delta.X);
        PreviewScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset + delta.Y);
    }

    private void PreviewScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeStart is not null)
            EndMarquee(e);
        if (_moveLastPoint is not null)
        {
            _moveLastPoint = null;
            ViewModel.EndMoveSelected();
        }

        EndPan();
    }

    // ---- Polygon editing (ver_0.1.1) ----

    private int? _draggingFreezeVertexIndex;

    /// <summary>While a text label is being dragged by its body: its anchor minus the point that was grabbed
    /// (page units), so each move can put the anchor at pointer + offset. Null for any other drag.</summary>
    private Vector? _textDragOffset;

    /// <summary>True while a text box's right edge is being dragged to change its width (text wraps to it).</summary>
    private bool _draggingTextWidth;

    /// <summary>The resize handle of the selected picture being dragged, or null.</summary>
    private ImageHandle? _draggingImageHandle;

    /// <summary>Roughly how close (in image-space units, before zoom) a click needs to be to an
    /// existing vertex/line to hit it instead of adding a new point or starting a pan. Divided by
    /// Zoom so the hit area feels the same physical size on screen regardless of zoom level.</summary>
    private double FreezeVertexHitTolerance => 10.0 / Math.Max(ViewModel.Zoom, 0.01);


    /// <summary>
    /// Converts a mouse event into IMAGE-SPACE coordinates (the same unscaled space each polygon's
    /// stored points use) directly relative to FreezeEditCanvas, then divides by Zoom. This works
    /// reliably because FreezeEditCanvas now has NO transform of its own and is explicitly
    /// sized/positioned to exactly match the image's actual on-screen (zoomed) bounds (see
    /// MainWindow.xaml and MainViewModel's ZoomedPreviewWidth/Height) — so GetPosition against it
    /// is unaffected by scrolling or by any transform, and needs no separate scroll-offset
    /// arithmetic.
    ///
    /// FreezeEditCanvas is also padded and offset (see MainViewModel.FreezeCanvasPaddingImageSpace)
    /// so points outside the image stay clickable/draggable — its own local (0,0) sits at image-
    /// space (-padding, -padding), so that padding is added back after converting to image-space.
    /// </summary>
    private Point GetImageSpacePosition(MouseEventArgs e) => CanvasToImageSpace(e.GetPosition(FreezeEditCanvas));

    /// <summary>Converts a point in FreezeEditCanvas's own coordinates (what a mouse event gives, or TranslatePoint) into page space.</summary>
    private Point CanvasToImageSpace(Point canvasPos)
    {
        double zoom = Math.Max(ViewModel.Zoom, 0.0001);
        double displayX = canvasPos.X / zoom - MainViewModel.FreezeCanvasPaddingImageSpace;
        double displayY = canvasPos.Y / zoom - MainViewModel.FreezeCanvasPaddingImageSpace;
        // If any Hide Row/Column is active, the DISPLAYED (gap-closed) coordinate space is
        // smaller than the ORIGINAL space every polygon's Vertices are stored in — this maps back
        // to that original space so hit-testing/add/drag all operate in the one consistent system.
        return ViewModel.MapDisplayToOriginal(displayX, displayY);
    }

    private void PreviewScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateFrozenOverlayPosition();

    /// <summary>The middle of the part of the page currently on screen, in page space — where a pasted line of plain text goes.</summary>
    private Point? GetViewportCenterImageSpace()
    {
        var middle = PreviewScrollViewer.TranslatePoint(
            new Point(PreviewScrollViewer.ActualWidth / 2, PreviewScrollViewer.ActualHeight / 2), FreezeEditCanvas);
        return CanvasToImageSpace(middle);
    }

    /// <summary>
    /// Keeps the floating text-options bar over the PREVIEW only: it starts to the right of the docked Effects panel (and its
    /// splitter strip) when there is one. The bar floats over the top of the preview rather than sitting in the layout — which is
    /// the whole point of it: showing or hiding it moves nothing else, so the page stays exactly where it is on the screen.
    /// </summary>
    private void UpdateTextOptionsBarMargin()
    {
        double docked = EffectsDockHost.ActualWidth;
        var barMargin = new Thickness(docked > 0 ? docked + EffectsDockSplitterWidth : 0, 0, 0, 0);
        TextOptionsBar.Margin = barMargin;
        ImageOptionsBar.Margin = barMargin;
    }

    /// <summary>
    /// Positions every frozen polygon's pinned overlay by asking WPF directly "where does
    /// image-space (0,0) currently render on screen?" via TransformToVisual, rather than manually
    /// replicating the padding/centering/zoom math — the same category of bug (zoom drift,
    /// click-position mismatch) that came from doing that by hand earlier in this project.
    ///
    /// FreezeEditCanvas's own local (0,0) is offset by -FreezeCanvasPaddingImageSpace in image
    /// space (see GetImageSpacePosition), so image-space (0,0) is actually at FreezeEditCanvas
    /// local (paddingScaled, paddingScaled) — that's the point queried below. This screen position
    /// (origin) is the SAME for every polygon regardless of freeze direction; what differs is which
    /// axis then gets "cancelled" back out to keep it pinned versus left alone to track scroll:
    ///   - Freeze Row: X used as-is (tracks horizontal scroll, so the band shifts left/right in
    ///     sync with the rest of the page); Y has VerticalOffset added back in, cancelling the
    ///     vertical-scroll component so the row stays pinned regardless of how far scrolled down.
    ///   - Freeze Column: the mirror image — Y used as-is (tracks vertical scroll); X has
    ///     HorizontalOffset added back in, so the column stays pinned regardless of horizontal scroll.
    /// </summary>
    private void UpdateFrozenOverlayPosition()
    {
        var frozenPolygons = ViewModel.Polygons.Where(p => p.IsFrozen).ToList();
        if (frozenPolygons.Count == 0)
            return; // nothing to position — skip the TransformToVisual call entirely

        try
        {
            double paddingScaled = MainViewModel.FreezeCanvasPaddingImageSpace * ViewModel.Zoom;
            var transform = FreezeEditCanvas.TransformToVisual(PreviewOuterGrid);
            var origin = transform.Transform(new Point(paddingScaled, paddingScaled));

            foreach (var polygon in frozenPolygons)
            {
                if (polygon.IsFrozenRow)
                {
                    polygon.FrozenOverlayOffsetX = origin.X;
                    polygon.FrozenOverlayOffsetY = origin.Y + PreviewScrollViewer.VerticalOffset;
                }
                else // IsFrozenColumn
                {
                    polygon.FrozenOverlayOffsetX = origin.X + PreviewScrollViewer.HorizontalOffset;
                    polygon.FrozenOverlayOffsetY = origin.Y;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Visual tree not fully connected yet (e.g. during initial layout) — a later call
            // (Loaded, or the next ScrollChanged/property change) will succeed.
        }
    }

    private void FreezeEditCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsEditingPolygon) return;
        ReleaseToolbarFocus();
        if (_inlineTextBox is { IsMouseOver: true }) return; // a click in the text editor belongs to the editor, not the canvas under it

        var pos = GetImageSpacePosition(e);

        if (ViewModel.IsDrawingShape)
        {
            if (ViewModel.IsPlacingText)
            {
                PlaceTextAt(pos);
            }
            else
            {
                ViewModel.HandleShapeClick(pos.X, pos.Y, Keyboard.Modifiers); // Ctrl/Shift held at THIS click change how an ellipse is built
            }
            e.Handled = true;
            return;
        }

        // A selected picture: one of its eight handles starts a resize (checked first, since a handle sits on the box's edge, inside the grab zone of its
        // body), and anywhere else on the picture picks it up to move — keeping the offset from the anchor, like text.
        if (e.ClickCount == 1 && ViewModel.EditablePolygon is { IsImage: true } picture)
        {
            if (ViewModel.HitImageHandle(picture, pos.X, pos.Y) is { } handle)
            {
                _draggingImageHandle = handle;
                ViewModel.BeginImageResize(picture);
                FreezeEditCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            if (picture.Vertices.Count > 0 && ViewModel.IsPointNearPolygonLine(picture, pos.X, pos.Y, FreezeVertexHitTolerance))
            {
                _textDragOffset = new Vector(picture.Vertices[0].X - pos.X, picture.Vertices[0].Y - pos.Y);
                _draggingFreezeVertexIndex = 0;
                ViewModel.BeginVertexDrag(0);
                FreezeEditCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // The right edge of a selected text box is its width handle: dragging it sets the width the text wraps to.
        // Checked before the body drag below, since the handle's grab zone overlaps the box's right edge.
        if (e.ClickCount == 1 && ViewModel.EditablePolygon is { IsText: true } sizing &&
            ViewModel.IsOnTextWidthHandle(sizing, pos.X, pos.Y, FreezeVertexHitTolerance))
        {
            _draggingTextWidth = true;
            FreezeEditCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        // A selected text label is moved by grabbing it ANYWHERE inside its rectangle (it has no point handle to
        // find). The anchor-to-pointer offset is kept, so the text moves with the pointer instead of jumping so
        // its corner is under it. Everything else about a drag (one undo step, mouse capture, the move/up handlers)
        // is the ordinary vertex drag of its one and only point.
        if (e.ClickCount == 1 && ViewModel.EditablePolygon is { IsText: true } text && text.Vertices.Count > 0 &&
            ViewModel.IsPointNearPolygonLine(text, pos.X, pos.Y, FreezeVertexHitTolerance))
        {
            _textDragOffset = new Vector(text.Vertices[0].X - pos.X, text.Vertices[0].Y - pos.Y);
            _draggingFreezeVertexIndex = 0;
            ViewModel.BeginVertexDrag(0);
            FreezeEditCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        // The grab radius is the FULL FreezeVertexHitTolerance — the same radius inside which
        // AddPolygonVertex refuses to create a new point (the "isolated" zone around each vertex).
        // That zone used to be dead space: too far from the vertex to grab it (a tighter 0.4x radius
        // was used for grabbing), yet too close to add a point, so a click-drag that landed in it just
        // silently did nothing. Now a press anywhere in that zone grabs the nearest vertex exactly as
        // if it had landed on the vertex itself, so there is no dead ring left around a vertex.
        int hitIndex = ViewModel.FindNearestVertexIndex(pos.X, pos.Y, FreezeVertexHitTolerance);

        if (e.ClickCount >= 2)
        {
            // Double-click on an existing vertex removes it; double-click on empty space does
            // nothing (no accidental vertex-add from the second click of a double-click).
            if (hitIndex >= 0)
                ViewModel.RemovePolygonVertex(hitIndex);
            e.Handled = true;
            return;
        }

        if (hitIndex >= 0)
        {
            _draggingFreezeVertexIndex = hitIndex;
            ViewModel.BeginVertexDrag(hitIndex); // ONE undo snapshot for the whole upcoming drag, not per-move-tick
            FreezeEditCanvas.CaptureMouse();
        }
        else
        {
            ViewModel.AddPolygonVertex(pos.X, pos.Y, FreezeVertexHitTolerance);
        }

        e.Handled = true;
    }

    // ---- In-place text editing ----
    // Text is typed directly on the page: a TextBox, set up like the text it covers (same font, weight, style, underline,
    // wrapping width, rotation, and size at the current zoom), is put over the annotation. It lives in FreezeEditCanvas —
    // the one layer that is interactive, and always is while a text annotation is selected. The editor itself is
    // transparent and borderless: the annotation's own dashed selection rectangle (the very same one shown when it is merely
    // selected) stays visible around it, and the page behind is blurred a little more than usual (PolygonViewModel's
    // IsInlineEditing), so what's typed stays readable without a solid box over the page.
    //
    // Keys: Enter starts a new line (text can be several lines now); Ctrl+Enter, Escape, or clicking anywhere else finishes and
    // keeps the text. The annotation follows what is typed (so its box and backdrop grow with it) but is only saved on finishing.
    // Character spacing and exact line spacing aren't applied while typing (a TextBox can't) — they show again on finishing.

    private TextBox? _inlineTextBox;
    private PolygonViewModel? _inlineTextPolygon;
    private bool _inlineTextIsNew;
    private string _inlineOriginalText = "";

    /// <summary>The text tool's click: creates the (empty) text where the page was clicked and opens the editor on it.</summary>
    private void PlaceTextAt(Point pos)
    {
        var polygon = ViewModel.PlaceEmptyText(pos.X, pos.Y);
        if (polygon is not null)
            BeginInlineTextEdit(polygon, isNew: true);
    }

    /// <summary>Opens the editor over a text annotation — for a new one (<paramref name="isNew"/>: leaving it empty
    /// discards it), or an existing one (double-click, or right-click > Edit Text).</summary>
    private void BeginInlineTextEdit(PolygonViewModel polygon, bool isNew)
    {
        EndInlineTextEdit(commit: true); // at most one editor at a time
        if (!polygon.IsText || polygon.DisplayVertices.Count == 0) return;

        var anchor = polygon.DisplayVertices[0];
        bool fixedWidth = polygon.BoxWidthPoints > 0;
        var box = new TextBox
        {
            Text = polygon.Text,
            FontFamily = new FontFamily(polygon.FontFamilyName),
            FontSize = Math.Max(6, polygon.DisplayFontSize),
            FontWeight = polygon.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = polygon.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = polygon.EditorForeground,
            CaretBrush = polygon.EditorForeground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextAlignment = polygon.Alignment switch
            {
                TextAlign.Center => TextAlignment.Center,
                TextAlign.Right => TextAlignment.Right,
                TextAlign.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            TextWrapping = fixedWidth ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Rotates about the top-left, exactly like the annotation's own box.
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = new RotateTransform(polygon.RotationDegrees),
        };
        if (fixedWidth)
            box.Width = polygon.TextDisplayWidth;
        else
            box.MinWidth = Math.Max(20, polygon.TextDisplayWidth);
        if (polygon.Underline)
            box.TextDecorations = TextDecorations.Underline;
        if (Math.Abs(polygon.LineSpacing - 1.0) > 0.01)
        {
            TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
            TextBlock.SetLineHeight(box, polygon.DisplayLineHeight);
        }

        // Canvas-local = display position + the canvas's padding (see GetImageSpacePosition). The editor's text starts
        // exactly where the annotation's text does (no padding of its own).
        Canvas.SetLeft(box, anchor.X + ViewModel.FreezeCanvasScaledPadding);
        Canvas.SetTop(box, anchor.Y + ViewModel.FreezeCanvasScaledPadding);

        box.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                EndInlineTextEdit(commit: true);
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                // Esc keeps what was typed, like finishing (Ctrl+Z undoes it afterwards if it was a mistake). It never discards.
                EndInlineTextEdit(commit: true);
                args.Handled = true;
            }
        };
        box.TextChanged += (_, _) =>
        {
            if (ReferenceEquals(_inlineTextPolygon, polygon))
                ViewModel.PreviewInlineText(polygon, box.Text);
        };
        box.LostKeyboardFocus += (_, _) => EndInlineTextEdit(commit: true);

        _inlineTextBox = box;
        _inlineTextPolygon = polygon;
        _inlineTextIsNew = isNew;
        _inlineOriginalText = polygon.Text;
        polygon.IsInlineEditing = true;
        ViewModel.RefreshTextDisplay(polygon); // the backdrop blur is stronger while editing
        FreezeEditCanvas.Children.Add(box);

        // Focus after the click that started this has finished — doing it now would lose to that click's own focus handling.
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Closes the editor, applying (commit) or discarding its text. Safe to call when none is open, and
    /// re-entrantly: taking the editor out of the canvas makes it lose focus, which calls this again.</summary>
    private void EndInlineTextEdit(bool commit)
    {
        var box = _inlineTextBox;
        var polygon = _inlineTextPolygon;
        if (box is null || polygon is null) return;

        _inlineTextBox = null; // cleared first, so the focus-loss this causes (below) finds nothing to do
        _inlineTextPolygon = null;
        string text = box.Text;
        FreezeEditCanvas.Children.Remove(box);

        if (commit)
            ViewModel.CommitInlineText(polygon, text, _inlineTextIsNew, _inlineOriginalText);
        else
            ViewModel.CancelInlineText(polygon, _inlineTextIsNew, _inlineOriginalText);

        Keyboard.ClearFocus(); // so the single-key shortcuts work again straight away
    }

    private void FreezeEditCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel.IsDrawingShape)
        {
            // Live rubber-band preview: updates on every move (not just while a button is held),
            // so the rectangle visibly tracks the cursor between the first and second click.
            var rectPos = GetImageSpacePosition(e);
            ViewModel.UpdateShapePreview(rectPos.X, rectPos.Y, Keyboard.Modifiers);
            return;
        }

        if (_draggingImageHandle is ImageHandle draggedHandle)
        {
            if (e.LeftButton == MouseButtonState.Pressed && ViewModel.EditablePolygon is { IsImage: true } resizingPicture)
            {
                var handlePos = GetImageSpacePosition(e);
                ViewModel.ResizeImageTo(resizingPicture, draggedHandle, handlePos.X, handlePos.Y);
            }
            return;
        }

        if (_draggingTextWidth)
        {
            if (e.LeftButton == MouseButtonState.Pressed && ViewModel.EditablePolygon is { IsText: true } resizing)
            {
                var widthPos = GetImageSpacePosition(e);
                ViewModel.DragTextWidthTo(resizing, widthPos.X, widthPos.Y);
            }
            return;
        }

        if (_draggingFreezeVertexIndex is null)
        {
            // Cursors that say what a drag here would do on the selected text: a double arrow on the right edge (change
            // the width), a four-way arrow over the box (move it).
            var hover = GetImageSpacePosition(e);
            Cursor? hoverCursor = null;
            if (_inlineTextBox is null && ViewModel.EditablePolygon is { IsText: true } label)
            {
                if (ViewModel.IsOnTextWidthHandle(label, hover.X, hover.Y, FreezeVertexHitTolerance))
                    hoverCursor = Cursors.SizeWE;
                else if (ViewModel.IsPointNearPolygonLine(label, hover.X, hover.Y, FreezeVertexHitTolerance))
                    hoverCursor = Cursors.SizeAll;
            }
            // On a selected picture: a resize arrow on a handle (turned to match the picture's rotation), a four-way arrow over its body.
            if (ViewModel.EditablePolygon is { IsImage: true } hoverPicture)
            {
                if (ViewModel.HitImageHandle(hoverPicture, hover.X, hover.Y) is { } hoverHandle)
                    hoverCursor = CursorForHandle(hoverHandle, hoverPicture.RotationDegrees);
                else if (ViewModel.IsPointNearPolygonLine(hoverPicture, hover.X, hover.Y, FreezeVertexHitTolerance))
                    hoverCursor = Cursors.SizeAll;
            }
            FreezeEditCanvas.Cursor = hoverCursor;
            return;
        }

        if (_draggingFreezeVertexIndex is not int index || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = GetImageSpacePosition(e);
        if (_textDragOffset is Vector grab)
            ViewModel.MovePolygonVertex(index, pos.X + grab.X, pos.Y + grab.Y);
        else
            ViewModel.MovePolygonVertex(index, pos.X, pos.Y);
    }

    private void FreezeEditCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingFreezeVertexIndex is not null)
            ViewModel.EndVertexDrag();
        if (_draggingTextWidth)
            ViewModel.EndTextWidthDrag();
        if (_draggingImageHandle is not null)
            ViewModel.EndImageResize();
        _draggingImageHandle = null;
        _draggingTextWidth = false;
        _draggingFreezeVertexIndex = null;
        _textDragOffset = null;
        FreezeEditCanvas.ReleaseMouseCapture();
    }

    /// <summary>
    /// Enter closes the polygon currently being drawn into a loop and finishes/saves it. Escape
    /// finishes it too, leaving it open (a shape still waiting for clicks is dropped instead). Ctrl+Z / Ctrl+Y undo and redo
    /// (Ctrl+C/X/V, Ctrl+A likewise copy, cut, paste, select all). Delete removes every currently
    /// selected polygon (guarded against firing while a TextBox has focus, since Delete is also a
    /// normal text-editing key there — e.g. the Rows/Columns/Margin fields further down). Wired at
    /// the Window level (PreviewKeyDown tunnels from the root regardless of which control
    /// currently has focus) so these shortcuts work reliably.
    ///
    /// Every other shortcut below (Open Image, Open Job, Zoom In/Out, Fit Width/Height, Rotate
    /// Left/Right, Toggle Effects, Settings, Polygon, Rectangle, Print) is customizable via
    /// Settings > Keyboard Shortcuts (ViewModel.Shortcuts) rather than hardcoded — see
    /// KeyboardShortcutFormat.Matches for how a stored token like "Ctrl+Left" or "OemPlus" is
    /// compared against a live key event. All of them except Print are skipped while a TextBox has
    /// focus (typing a job/preset name, a margin value, etc. shouldn't accidentally trigger one of
    /// these plain-letter or Ctrl+Arrow shortcuts) — Print (Ctrl+P) isn't a standard text-editing
    /// shortcut, so it stays available everywhere, matching how it already worked before shortcuts
    /// became customizable.
    /// </summary>
    /// <summary>The release half of the ellipse Ctrl/Shift handling in Window_PreviewKeyDown.</summary>
    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = KeyboardShortcutFormat.ResolveKey(e);
        if (ViewModel.IsDrawingEllipse && key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
            ViewModel.RefreshShapePreview(Keyboard.Modifiers);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While text is being typed in place, every key (Delete, Escape, Enter, shortcuts...) belongs to that editor.
        if (_inlineTextBox is not null) return;

        var key = KeyboardShortcutFormat.ResolveKey(e);
        var modifiers = Keyboard.Modifiers;
        var shortcuts = ViewModel.Shortcuts;

        // Mid-ellipse: Ctrl (circle) and Shift (from the centre) should show up in the rubber-band the
        // instant they're pressed, not only once the mouse next moves. Never handled — they're still
        // modifiers for everything else.
        if (ViewModel.IsDrawingEllipse && key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
            ViewModel.RefreshShapePreview(modifiers);

        if (KeyboardShortcutFormat.Matches(shortcuts.Print, modifiers, key))
        {
            PrintButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is not TextBox)
        {
            if (KeyboardShortcutFormat.Matches(shortcuts.OpenImage, modifiers, key))
            {
                OpenImageButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.OpenJob, modifiers, key))
            {
                OpenJobButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.ZoomIn, modifiers, key))
            {
                ViewModel.Zoom *= ViewModel.ZoomWheelSensitivity;
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.ZoomOut, modifiers, key))
            {
                ViewModel.Zoom /= ViewModel.ZoomWheelSensitivity;
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.FitWidth, modifiers, key))
            {
                FitWidthButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.FitHeight, modifiers, key))
            {
                FitHeightButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.RotateLeft, modifiers, key))
            {
                RotateCounterclockwiseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.RotateRight, modifiers, key))
            {
                RotateClockwiseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.ToggleEffects, modifiers, key))
            {
                EffectsButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.Settings, modifiers, key))
            {
                SettingsButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.Polygon, modifiers, key) && ViewModel.NewPolygonCommand.CanExecute(null))
            {
                ViewModel.NewPolygonCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.Rectangle, modifiers, key) && ViewModel.NewRectangleCommand.CanExecute(null))
            {
                ViewModel.NewRectangleCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (KeyboardShortcutFormat.Matches(shortcuts.Export, modifiers, key))
            {
                ExportCurrentPageButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        // Undo / redo / cut / copy / paste / select all: the standard keys, not customisable, and left alone while a text box has
        // focus (there they edit that text). Undo while drawing takes back the last point; otherwise it steps back through the
        // page's annotation edits (see MainViewModel's history).
        //
        // A focused text box keeps the keys that edit its text. The exception is undo and redo (Z / Y) when that box has nothing of its own
        // to undo or redo — a toolbar number box that was merely clicked into, say. There the keys belong to the page; otherwise Ctrl+Z
        // quietly did nothing because focus was parked in a box that had no history, while the Undo button (which takes no focus) worked.
        bool textBoxKeepsKey = Keyboard.FocusedElement is TextBox focusedBox
                               && !(key is Key.Z or Key.Y && !focusedBox.CanUndo && !focusedBox.CanRedo);
        if (!textBoxKeepsKey && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            RelayCommand? command = null;
            if (modifiers == ModifierKeys.Control)
            {
                command = key switch
                {
                    Key.Z => ViewModel.UndoCommand,
                    Key.Y => ViewModel.RedoCommand,
                    Key.C => ViewModel.CopyCommand,
                    Key.X => ViewModel.CutCommand,
                    Key.V => ViewModel.PasteCommand,
                    _ => null,
                };
                if (key == Key.A)
                {
                    ViewModel.SelectAllPolygons();
                    e.Handled = true;
                    return;
                }
            }
            else if (key == Key.Z)
            {
                command = ViewModel.RedoCommand; // Ctrl+Shift+Z
            }

            if (command is not null)
            {
                if (command.CanExecute(null)) command.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Esc, with nothing being drawn, deselects: one annotation, or everything a selection rectangle picked up. With nothing
        // left selected it leaves the Select or Move mode. (Not while a text box has focus: there Esc isn't ours.)
        if (e.Key == Key.Escape && !ViewModel.IsDrawingPolygon && Keyboard.FocusedElement is not TextBox)
        {
            if (ViewModel.Polygons.Any(p => p.IsSelected))
            {
                ViewModel.ClearPolygonSelection();
                e.Handled = true;
                return;
            }
            if (ViewModel.IsMoveMode || ViewModel.IsSelectMode)
            {
                ViewModel.IsMoveMode = false;
                ViewModel.IsSelectMode = false;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && ViewModel.DeleteSelectedPolygonsCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedPolygonsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!ViewModel.IsDrawingPolygon) return;

        if (e.Key == Key.Enter)
        {
            if (ViewModel.ClosePolygonCommand.CanExecute(null))
                ViewModel.ClosePolygonCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc leaves what has been drawn as it is: a polygon or curve is FINISHED, left open (the same as the Finish button).
            // It used to cancel, throwing the drawing away — Delete is for removing things. Only a shape still waiting for its
            // remaining clicks has nothing worth keeping, and is dropped.
            if (ViewModel.IsDrawingFreeformPolygon && ViewModel.FinishPolygonCommand.CanExecute(null))
                ViewModel.FinishPolygonCommand.Execute(null);
            else
                ViewModel.CancelPolygonEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (KeyboardShortcutFormat.Matches(shortcuts.FinishPolygonOpen, modifiers, key)
                 && ViewModel.FinishPolygonCommand.CanExecute(null))
        {
            ViewModel.FinishPolygonCommand.Execute(null);
            e.Handled = true;
        }
    }
}
