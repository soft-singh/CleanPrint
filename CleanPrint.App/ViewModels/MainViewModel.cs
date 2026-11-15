using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using CleanPrint.App.Services;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Export;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;
using CleanPrint.Core.Services;
using Microsoft.Extensions.Logging;

namespace CleanPrint.App.ViewModels;

/// <summary>
/// Loads pages from a Watcher-produced job folder (Step 4) and reconnects the enhancement,
/// splitting, and printing pipeline (Step 5). Capture itself is entirely the Watcher's concern —
/// this ViewModel only ever reads finished PNGs from disk.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IImageProcessingService _imageProcessing;
    private readonly IPageSplittingService _pageSplitting;
    private readonly IPrintingService _printingService;
    private readonly PolygonAnnotationStore _polygonStore;
    private readonly ILogger<MainViewModel> _logger;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly List<string> _pageFilePaths = new();
    private string _jobId = string.Empty;

    /// <summary>The currently-loaded job's folder name (its ID) — read-only, exposed so code-behind
    /// can figure out "which job is this, relative to every other job on disk" for the Previous/
    /// Next Job navigation buttons, without needing its own separate tracking of the same thing.</summary>
    public string CurrentJobId => _jobId;

    /// <summary>Per-page split-setting overrides, keyed by document page index. Pages with no
    /// entry here fall back to the global SplitRows/SplitColumns/OverlapPixels values.</summary>
    private readonly Dictionary<int, SplitSettings> _pageOverrides = new();

    /// <summary>Guards the override fields against writing back into _pageOverrides while
    /// LoadPageAsync is populating them from a page that's being navigated TO (as opposed to the
    /// user actively editing them for the current page).</summary>
    private bool _suppressOverrideWrite;

    private CapturedPage? _currentRawPage;
    private CapturedPage? _currentWhitenedPage;
    private IReadOnlyList<SplitTile> _currentSplitTiles = Array.Empty<SplitTile>();

    /// <summary>Parallel label list for _currentSplitTiles, kept so preview mockups can be rebuilt
    /// from print-setting changes (paper size/orientation/scale/fit/margins) without re-splitting
    /// or re-whitening — only RefreshSplitPreviewMockups() needs to re-run, which is cheap.</summary>
    private IReadOnlyList<string> _currentSplitTileLabels = Array.Empty<string>();

    /// <summary>Loaded once per job (see LoadJob), holds every polygon for every page and variant
    /// already defined in this job. Saved back to disk after edits (see SavePolygonsForCurrentPageAsync).</summary>
    private JobAnnotationSettings _jobAnnotationSettings = new();

    /// <summary>Non-null only for jobs with a capture-info.json sidecar (Watcher-captured jobs —
    /// see JobCaptureInfo's own doc comment) — passed to every PngPageLoader.Load call for the
    /// current job so pages read their DPI from here rather than potentially-missing embedded
    /// metadata. Reloaded once per job in LoadJob, not per page.</summary>
    private int? _capturedDpiOverride;

    public MainViewModel(
        IImageProcessingService imageProcessing,
        IPageSplittingService pageSplitting,
        IPrintingService printingService,
        PolygonAnnotationStore polygonStore,
        ILogger<MainViewModel> logger)
    {
        _imageProcessing = imageProcessing;
        _pageSplitting = pageSplitting;
        _printingService = printingService;
        _polygonStore = polygonStore;
        _logger = logger;

        NextPageCommand = new RelayCommand(async _ => await ChangePageAsync(+1), _ => !IsBusy && CanGoNext);
        PreviousPageCommand = new RelayCommand(async _ => await ChangePageAsync(-1), _ => !IsBusy && CanGoPrevious);
        FirstPageCommand = new RelayCommand(async _ => await GoToPageAsync(1), _ => !IsBusy && CanGoPrevious);
        LastPageCommand = new RelayCommand(async _ => await GoToPageAsync(_pageFilePaths.Count), _ => !IsBusy && CanGoNext);
        ToggleBeforeAfterCommand = new RelayCommand(_ => ShowBefore = !ShowBefore);
        // The preview moves through the pages of the print's PAGE RANGE (see _printPreviewPages), not the whole job.
        PrintPreviewPreviousCommand = new RelayCommand(async _ => await StepPrintPreviewAsync(-1), _ => PrintPageRange.Position(_printPreviewPages, _printPreviewPageIndex) > 1);
        PrintPreviewNextCommand = new RelayCommand(async _ => await StepPrintPreviewAsync(+1), _ =>
        {
            int position = PrintPageRange.Position(_printPreviewPages, _printPreviewPageIndex);
            return position > 0 && position < _printPreviewPages.Count;
        });
        ApplySplitCommand = new RelayCommand(_ => ApplySplitCurrentPage(), _ => !IsBusy && _currentWhitenedPage is not null);
        ApplySplitAllPagesCommand = new RelayCommand(async _ => await ApplySplitAllPagesAsync(), _ => !IsBusy && _pageFilePaths.Count > 0);
        PrintAllCommand = new RelayCommand(async _ => await PrintAllAsync(),
            _ => !IsBusy && SelectedPrinter is not null && (PrintWholePage ? _pageFilePaths.Count > 0 : _currentSplitTiles.Count > 0));

        NewPolygonCommand = new RelayCommand(_ => BeginNewPolygon());
        NewRectangleCommand = new RelayCommand(_ => BeginNewShape(ShapeKind.Polygon)); // ShapeKind.Polygon == rectangle in shape mode
        NewEllipseCommand = new RelayCommand(_ => BeginNewShape(ShapeKind.Ellipse));
        NewCircleCenterCommand = new RelayCommand(_ => BeginNewShape(ShapeKind.CircleCenter));
        NewCircle3Command = new RelayCommand(_ => BeginNewShape(ShapeKind.Circle3));
        NewArcCommand = new RelayCommand(_ => BeginNewShape(ShapeKind.Arc3));
        NewCurveCommand = new RelayCommand(_ => BeginNewPolygon(ShapeKind.Curve));
        NewTextCommand = new RelayCommand(_ => BeginNewShape(ShapeKind.Text));
        UndoCommand = new RelayCommand(async _ => await UndoAsync(), _ => CanUndoNow);
        RedoCommand = new RelayCommand(async _ => await StepHistoryAsync(+1), _ => CanRedoNow);
        CopyCommand = new RelayCommand(_ => CopySelection(), _ => HasPastableSelection);
        CutCommand = new RelayCommand(_ => CutSelection(), _ => HasPastableSelection);
        PasteCommand = new RelayCommand(_ => PasteFromClipboard(), _ => CanPasteNow);
        MirrorHorizontalCommand = new RelayCommand(_ => MirrorSelection(horizontal: true), _ => HasMirrorableSelection);
        MirrorVerticalCommand = new RelayCommand(_ => MirrorSelection(horizontal: false), _ => HasMirrorableSelection);
        FinishPolygonCommand = new RelayCommand(async _ => await FinishPolygonAsync(), _ => IsDrawingFreeformPolygon);
        ClosePolygonCommand = new RelayCommand(async _ => await ClosePolygonAsync(), _ => CanCloseCurrentPolygon);
        CancelPolygonEditCommand = new RelayCommand(_ => CancelPolygonEdit(), _ => IsDrawingPolygon);
        DeleteSelectedPolygonsCommand = new RelayCommand(async _ => await DeleteSelectedPolygonsAsync(), _ => Polygons.Any(p => p.IsSelected));

        foreach (var name in _printingService.GetInstalledPrinterNames())
            PrinterNames.Add(name);
        SelectedPrinter = PrinterNames.FirstOrDefault();

        ApplyPersistedSettings();

        // "Applied automatically" — every effect change reprocesses the current page and refreshes
        // the preview, debounced (see DebounceReprocessCurrentPage) so dragging a slider doesn't
        // trigger a full reprocess on every intermediate tick. Suppressed during LoadJob's own
        // EffectSettings.CopyFrom calls (see _suppressEffectReprocessOnChange) — LoadJob always
        // calls LoadPageAsync right after applying settings, which already correctly builds the NEW
        // page with those settings; without suppressing this, CopyFrom's own PropertyChanged
        // events would ALSO schedule a debounced reprocess here, which — since it operates on
        // whatever page happened to still be current when its 300ms delay elapsed — could fire
        // against the OLD job's still-loaded image using the NEW job's already-swapped-in polygon
        // data, painting the new polygons over the old image until LoadPageAsync itself finished.
        EffectSettings.PropertyChanged += (_, _) =>
        {
            if (_suppressEffectReprocessOnChange) return;
            DebounceReprocessCurrentPage();
        };
    }

    private bool _suppressEffectReprocessOnChange;
    private CancellationTokenSource? _effectSettingsDebounceCts;

    /// <summary>Cancels any pending reprocess and schedules a new one ~300ms out — if another
    /// change comes in before that elapses, THIS one is cancelled in turn, so only the last change
    /// in a burst (e.g. dragging a slider) actually triggers the (comparatively expensive, since it
    /// re-runs the whole effects pipeline) reprocess.</summary>
    private async void DebounceReprocessCurrentPage()
    {
        _effectSettingsDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _effectSettingsDebounceCts = cts;

        try
        {
            await Task.Delay(EffectsDebounceMilliseconds, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer change
        }

        if (cts.IsCancellationRequested) return;
        await ReprocessCurrentPageAsync();
    }

    /// <summary>Re-runs the effects pipeline (and auto-crop, if enabled) on the ALREADY-LOADED raw
    /// page — no disk re-read needed, unlike LoadPageAsync — then refreshes everything downstream
    /// (original dimensions, polygon positions, the preview image itself) exactly the way
    /// LoadPageAsync does after its own version of this same sequence.</summary>
    private async Task ReprocessCurrentPageAsync()
    {
        if (_currentRawPage is null) return;

        _currentWhitenedPage = await _imageProcessing.ApplyEffectsAsync(_currentRawPage, EffectSettings);
        if (EffectSettings.AutoCropEnabled)
            _currentWhitenedPage = await _imageProcessing.CropToContentAsync(_currentWhitenedPage, EffectSettings.AutoCropWhiteThreshold, EffectSettings.AutoCropPaddingPixels);

        UpdateOriginalPageDimensions();
        RefreshPolygonsForCurrentPageAndVariant();
        RefreshPreviewImage();
        SaveCurrentJobState();
    }

    /// <summary>Persists the CURRENTLY-loaded job's effect settings + zoom to its own job-state.json
    /// (see JobViewStateService) — so reopening this exact job later restores them instead of
    /// falling back to the default-preset logic, which only applies the very first time a job is
    /// ever opened. No-ops if no job is currently loaded. Public so MainWindow can call it on app
    /// exit too, in case the 300ms debounce for a very last-second change hasn't fired yet.</summary>
    public void SaveCurrentJobState()
    {
        if (string.IsNullOrEmpty(_jobId)) return;
        string jobFolder = CleanPrintPaths.JobFolder(_jobId);
        JobViewStateService.Save(jobFolder, new JobViewState { EffectSettings = EffectSettings.Clone(), Zoom = Zoom });
    }

    private CancellationTokenSource? _zoomSaveDebounceCts;

    /// <summary>Same debounce shape as DebounceReprocessCurrentPage, but for persisting Zoom
    /// specifically (Ctrl+scroll can fire many rapid changes in a row) — saving doesn't need to
    /// reprocess anything, just write job-state.json once things settle.</summary>
    private async void DebounceSaveZoom()
    {
        _zoomSaveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _zoomSaveDebounceCts = cts;

        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;
        SaveCurrentJobState();
    }

    /// <summary>The current, live, non-destructively-applied image effect settings — the SAME
    /// instance the Effects dialog binds to directly, so changing a slider there immediately
    /// reflects here (and vice versa) without needing an explicit sync step. Reprocessed into the
    /// current page automatically whenever any of its properties change — see the constructor's
    /// PropertyChanged subscription below.</summary>
    public ImageEffectSettings EffectSettings { get; } = new();

    /// <summary>Every customizable keyboard shortcut — the SAME instance SettingsDialog's Keyboard
    /// Shortcuts tab binds to directly and MainWindow's key handler reads from, so recording a new
    /// shortcut there takes effect immediately without needing an explicit save/apply step. Loaded
    /// at startup in ApplyPersistedSettings; saved explicitly (see SaveShortcuts) whenever
    /// SettingsDialog closes, since these are edited far less often than most other settings and
    /// don't need the debounced-autosave treatment EffectSettings/Zoom get.</summary>
    public KeyboardShortcuts Shortcuts { get; } = new();

    /// <summary>Persists Shortcuts — called from SettingsDialog's Closing handler.</summary>
    public void SaveShortcuts() => KeyboardShortcutService.Save(Shortcuts);

    /// <summary>PNG compression level / JPG quality used both by Export (below) and, via the same
    /// persisted file, by the Watcher's own XPS-to-PNG capture step — see ExportQualitySettings'
    /// own doc comment. Saved explicitly whenever SettingsDialog closes, same as Shortcuts.</summary>
    public ExportQualitySettings ExportQuality { get; } = new();

    /// <summary>Persists ExportQuality — called from SettingsDialog's Closing handler.</summary>
    public void SaveExportQuality() => ExportQualitySettingsService.Save(ExportQuality);

    /// <summary>When on, every exported image's path is recorded into the job's own
    /// source-images.json (JobSourceService.AddSourcePath) — the same mechanism, and the same
    /// lookup, Open Image(s)'s duplicate check already uses for original source images. Off by
    /// default: even though this never writes anything near the exported file itself (all the
    /// bookkeeping lives inside the job's own AppData folder), it's still an extra thing happening
    /// on export that most people sending a one-off image elsewhere won't want. Set from
    /// SettingsDialog's General tab; persisted via AppSettings like most other simple settings.</summary>
    private bool _linkExportsToJobs;
    public bool LinkExportsToJobs
    {
        get => _linkExportsToJobs;
        set => SetField(ref _linkExportsToJobs, value);
    }

    /// <summary>Which preset (by name) to apply automatically whenever a new job is loaded — see
    /// LoadJob and AppSettings.DefaultPresetName's own doc comment. Null/empty means none
    /// configured. Set from SettingsDialog's General tab.</summary>
    private string? _defaultPresetName;
    public string? DefaultPresetName
    {
        get => _defaultPresetName;
        set => SetField(ref _defaultPresetName, value);
    }

    /// <summary>Multiplier applied per Ctrl+scroll wheel tick when zooming the preview (see
    /// MainWindow.xaml.cs's PreviewScrollViewer_PreviewMouseWheel) — 1.0 means "no zoom change per
    /// tick" and higher values zoom faster/more aggressively per tick. Set from SettingsDialog's
    /// General tab. Clamped to a sane range so an extreme value can't make zoom unusable (too close
    /// to 1.0 = imperceptibly slow; too high = a single tick jumps too far to control).</summary>
    private double _zoomWheelSensitivity = 1.1;
    public double ZoomWheelSensitivity
    {
        get => _zoomWheelSensitivity;
        set => SetField(ref _zoomWheelSensitivity, Math.Clamp(value, 1.01, 2.0));
    }

    /// <summary>Milliseconds to wait after the last effect-settings change before actually
    /// reprocessing the page (see DebounceReprocessCurrentPage) — lower values reflect a slider
    /// change sooner but reprocess more often while dragging; higher values wait longer but
    /// reprocess less. Set from SettingsDialog's General tab. Clamped to a sane range so an extreme
    /// value can't make the debounce pointless (too low) or feel unresponsive (too high).</summary>
    private int _effectsDebounceMilliseconds = 300;
    public int EffectsDebounceMilliseconds
    {
        get => _effectsDebounceMilliseconds;
        set => SetField(ref _effectsDebounceMilliseconds, Math.Clamp(value, 50, 2000));
    }

    /// <summary>"Toolbar scale": independent of Windows' own display scaling, a manual multiplier (1.0 = 100%)
    /// on the app's TOOLBARS — the drawing toolbar, and the buttons on the title bar and status bar — for
    /// whenever the OS's DPI scaling makes them feel larger or smaller than preferred. Separate from
    /// <see cref="DialogScale"/> on purpose: toolbars are dense, always-visible strips where a smaller size
    /// is wanted, while dialogs are things you read and want comfortable. Deliberately does NOT touch the main
    /// window's preview area — that's the one place in this app with genuinely fragile, carefully tuned
    /// screen-coordinate math (the Freeze Row/Column overlay), and layering an additional transform on top
    /// of it is not a risk worth taking for a cosmetic sizing preference. Allowed down to 60%, since a
    /// toolbar that's too big is the usual complaint; clamped so an extreme value can't make the controls
    /// unusably tiny or overflow the window.</summary>
    private double _uiScale = 1.0;
    public double UiScale
    {
        get => _uiScale;
        set => SetField(ref _uiScale, Math.Clamp(value, 0.6, 1.5));
    }

    /// <summary>"Dialog &amp; panel scale": the same kind of multiplier for everything that ISN'T a toolbar — the
    /// Effects panel, Print, Settings, Open Job and the small dialogs. Pushed into <see cref="UiScaleSource"/>,
    /// which those windows follow through the UiScaling attached property.</summary>
    private double _dialogScale = 1.0;
    public double DialogScale
    {
        get => _dialogScale;
        set
        {
            if (SetField(ref _dialogScale, Math.Clamp(value, 0.75, 1.5)))
                UiScaleSource.Instance.Scale = _dialogScale;
        }
    }

    // ---- Private mode: jobs that are not kept ----------------------------------------------------------------------------------------------------------------------------
    // Like a private browser window. While on, new jobs (captured, blank, opened image) live outside the job cache and are deleted when you leave them or close CleanPrint — see
    // PrivateJobService. Deliberately NOT remembered between runs: CleanPrint always starts in the normal mode, so a forgotten switch can never make tomorrow's jobs vanish.

    private bool _isPrivateMode;
    public bool IsPrivateMode
    {
        get => _isPrivateMode;
        set
        {
            if (!SetField(ref _isPrivateMode, value)) return;
            PrivateJobService.SetModeFlag(value);
            RaisePrivateStateChanged();
            StatusText = value
                ? "Private mode is ON: jobs from now on are not saved, and are deleted when you leave them or close Clean Print."
                : IsViewingPrivateJob ? "Private mode is off. The job on screen is still private: it is deleted when you open another job." : "Private mode is off.";
        }
    }

    /// <summary>True while the job on screen is a private one (it started in private mode — and stays private even if the mode is switched off, until you leave it).</summary>
    public bool IsViewingPrivateJob => CleanPrintPaths.IsPrivateJobId(_jobId);

    /// <summary>The purple strip under the title bar shows whenever private mode is on, or the job on screen is a private one (which will be deleted).</summary>
    public bool ShowPrivateBanner => _isPrivateMode || IsViewingPrivateJob;

    public string PrivateBannerText => _isPrivateMode
        ? (IsViewingPrivateJob
            ? "PRIVATE MODE — this job, and any new ones, are not saved. They are deleted when you open another job or close Clean Print."
            : "PRIVATE MODE — jobs captured or created from now on are not saved. They are deleted when you open another job or close Clean Print.")
        : "PRIVATE JOB — this job is not saved. It is deleted when you open another job or close Clean Print.";

    private void RaisePrivateStateChanged()
    {
        OnPropertyChanged(nameof(IsViewingPrivateJob));
        OnPropertyChanged(nameof(ShowPrivateBanner));
        OnPropertyChanged(nameof(PrivateBannerText));
    }

    /// <summary>The chosen theme's name (Settings > General > Appearance). Applied the moment it changes — App listens for this property and calls ThemeManager.Apply, so every open window follows live.
    /// Themes are defined in Core (ThemeCatalog); every standard control is styled from them (Themes/InputStyles.xaml), so a theme changes the whole look, not just backgrounds.</summary>
    private string _themeName = CleanPrint.Core.Theming.ThemeCatalog.Classic;
    public string ThemeName
    {
        get => _themeName;
        set
        {
            if (SetField(ref _themeName, CleanPrint.Core.Theming.ThemeCatalog.Find(value).Name))
                OnPropertyChanged(nameof(SelectedThemeOption));
        }
    }

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        CleanPrint.Core.Theming.ThemeCatalog.All.Select(t => new ThemeOption(t)).ToList();

    public ThemeOption SelectedThemeOption
    {
        get => ThemeOptions.First(o => o.Name == _themeName);
        set { if (value is not null) ThemeName = value.Name; }
    }

    // ---- Icon colour (Settings > General > Appearance) ----
    // The icons take a variation of the THEME's colour (light grey on the dark themes, a tinted grey on the light ones); this slider only makes them darker or lighter. There is no colour
    // picker: the theme decides the hue. App listens for changes and calls ThemeManager.SetIconLightness — the ViewModel itself never touches UI resources.

    private double _iconLightness;
    /// <summary>-100 (darkest) to +100 (lightest); 0 = the theme's own icon colour.</summary>
    public double IconLightness
    {
        get => _iconLightness;
        set => SetField(ref _iconLightness, Math.Clamp(Math.Round(value), -100, 100));
    }

    // ---- Silent export and the default export format (Settings > Capture & Export) ----

    /// <summary>The silent-export settings: edited in Settings, saved to a small file the Watcher reads for each job (see SilentExportSettings).</summary>
    public SilentExportSettings SilentExport { get; } = new();

    public void SaveSilentExport() => SilentExportSettingsService.Save(SilentExport);

    public IReadOnlyList<ImageFormatOption> ManualFormatOptions => ImageFormats.Manual;
    public IReadOnlyList<ImageFormatOption> SilentFormatOptions => ImageFormats.Silent;

    /// <summary>The format the Export dialogs offer first.</summary>
    public ImageFormatOption DefaultExportFormatOption
    {
        get => ImageFormats.Find(ImageFormats.Manual, ExportQuality.DefaultExportFormat);
        set { if (value is not null) ExportQuality.DefaultExportFormat = value.Id; }
    }

    /// <summary>The format silent export writes.</summary>
    public ImageFormatOption SilentExportFormatOption
    {
        get => ImageFormats.Find(ImageFormats.Silent, SilentExport.Format);
        set { if (value is not null) SilentExport.Format = value.Id; }
    }

    /// <summary>1-based index of the default export format in a Save dialog's filter list (PNG, JPEG) — what SaveFileDialog.FilterIndex wants.</summary>
    public int DefaultExportFilterIndex => ExportQuality.DefaultExportFormat == "jpg" ? 2 : 1;

    // ---- Job cache limits (edited in Settings > General; current usage also shown in Open Job) ----
    // Two independent, optional limits — a maximum number of jobs and a maximum total size — each
    // with its own on/off switch, plus a third switch (JobCacheShowWarning) for whether reaching one
    // is announced in the main window's status bar. Nothing is ever deleted automatically.

    private bool _jobCacheLimitCountEnabled;
    public bool JobCacheLimitCountEnabled
    {
        get => _jobCacheLimitCountEnabled;
        set { if (SetField(ref _jobCacheLimitCountEnabled, value)) UpdateJobCacheStatus(); }
    }

    private int _jobCacheMaxJobs = 100;
    public int JobCacheMaxJobs
    {
        get => _jobCacheMaxJobs;
        set { if (SetField(ref _jobCacheMaxJobs, Math.Max(1, value))) UpdateJobCacheStatus(); }
    }

    private bool _jobCacheLimitSizeEnabled;
    public bool JobCacheLimitSizeEnabled
    {
        get => _jobCacheLimitSizeEnabled;
        set { if (SetField(ref _jobCacheLimitSizeEnabled, value)) UpdateJobCacheStatus(); }
    }

    private int _jobCacheMaxSizeMb = 2048;
    public int JobCacheMaxSizeMb
    {
        get => _jobCacheMaxSizeMb;
        set { if (SetField(ref _jobCacheMaxSizeMb, Math.Max(10, value))) UpdateJobCacheStatus(); }
    }

    private bool _jobCacheShowWarning = true;
    /// <summary>Master switch for the main-window warning (Settings > General). Independent of the two
    /// limits: with this off, a reached limit simply isn't announced in the status bar.</summary>
    public bool JobCacheShowWarning
    {
        get => _jobCacheShowWarning;
        set { if (SetField(ref _jobCacheShowWarning, value)) OnPropertyChanged(nameof(HasJobCacheWarning)); }
    }

    private JobCacheUsage _jobCacheUsage;
    private bool _jobCacheUsageKnown;
    private bool _jobCacheMeasuring;
    private bool _jobCacheRemeasureRequested;

    private string _jobCacheWarning = "";
    /// <summary>Empty while no ENABLED limit has been reached; otherwise a one-line message naming
    /// which limit(s) were reached and by how much. Shown in the main window's status bar and in
    /// the Open Job dialog.</summary>
    public string JobCacheWarning
    {
        get => _jobCacheWarning;
        private set
        {
            if (SetField(ref _jobCacheWarning, value))
                OnPropertyChanged(nameof(HasJobCacheWarning));
        }
    }

    /// <summary>What the main window's status bar binds its visibility to: there IS a warning to show
    /// AND the person hasn't switched warnings off. (JobCacheWarning itself is still computed while
    /// switched off, so turning it back on shows the current state immediately.)</summary>
    public bool HasJobCacheWarning => _jobCacheShowWarning && _jobCacheWarning.Length > 0;

    private string _jobCacheUsageText = "Measuring job cache...";
    /// <summary>One line of current usage ("Currently using 12 job(s), 345.6 MB.") for Settings and the
    /// Open Job dialog, so how much of a limit is in use is visible right where it's being set.</summary>
    public string JobCacheUsageText
    {
        get => _jobCacheUsageText;
        private set => SetField(ref _jobCacheUsageText, value);
    }

    /// <summary>Recomputes JobCacheWarning from the last measured usage and the current limits —
    /// no disk access, so it's cheap enough to run on every limit/checkbox change. "Reached" means
    /// at or over the limit (>=), so a cache sitting exactly AT its limit already warns, before the
    /// next job would push it over.</summary>
    private void UpdateJobCacheStatus()
    {
        if (_jobCacheUsageKnown)
            JobCacheUsageText = $"Currently using {_jobCacheUsage.JobCount:N0} job(s), {JobCacheService.FormatSize(_jobCacheUsage.TotalBytes)}.";

        var reached = new List<string>();
        if (JobCacheLimitCountEnabled && _jobCacheUsage.JobCount >= JobCacheMaxJobs)
            reached.Add($"{_jobCacheUsage.JobCount:N0} of {JobCacheMaxJobs:N0} jobs");
        if (JobCacheLimitSizeEnabled && _jobCacheUsage.TotalMegabytes >= JobCacheMaxSizeMb)
            reached.Add($"{_jobCacheUsage.TotalMegabytes:N0} of {JobCacheMaxSizeMb:N0} MB");

        JobCacheWarning = reached.Count == 0
            ? ""
            : $"Job cache limit reached ({string.Join(", ", reached)}). Open the Job browser to delete old jobs.";
    }

    /// <summary>Re-measures the job cache folder on a background thread (it enumerates every file of
    /// every job, which can take a moment for a big cache) and refreshes the warning. Overlapping
    /// calls coalesce into at most one extra pass instead of piling up scans.</summary>
    public async Task RefreshJobCacheUsageAsync()
    {
        if (_jobCacheMeasuring)
        {
            _jobCacheRemeasureRequested = true;
            return;
        }

        _jobCacheMeasuring = true;
        try
        {
            do
            {
                _jobCacheRemeasureRequested = false;
                _jobCacheUsage = await Task.Run(JobCacheService.Measure);
                _jobCacheUsageKnown = true;
                UpdateJobCacheStatus();
            }
            while (_jobCacheRemeasureRequested);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Measuring the job cache failed.");
        }
        finally
        {
            _jobCacheMeasuring = false;
        }
    }

    /// <summary>Lets the Open Job dialog hand over a measurement it just made itself (it already
    /// enumerates every job to build its list), so the main window's warning updates right after a
    /// delete without a second full scan.</summary>
    public void ReportJobCacheUsage(JobCacheUsage usage)
    {
        _jobCacheUsage = usage;
        _jobCacheUsageKnown = true;
        UpdateJobCacheStatus();
    }

    /// <summary>
    /// Restores the split &amp; print controls to however they were last left (see
    /// AppSettingsService) — called once at the end of the constructor, after the printer list and
    /// its default paper size are already populated above, since restoring the saved printer name
    /// (if it's still installed) needs to happen before restoring the saved paper size name (which
    /// is matched against whatever THAT printer reports supporting).
    /// </summary>
    private void ApplyPersistedSettings()
    {
        // Loaded independently of AppSettings below (separate file/service) since it has its own
        // "nothing saved yet" case that shouldn't skip restoring everything else.
        var savedShortcuts = KeyboardShortcutService.Load();
        if (savedShortcuts is not null)
            Shortcuts.CopyFrom(savedShortcuts);

        var savedExportQuality = ExportQualitySettingsService.Load();
        if (savedExportQuality is not null)
        {
            ExportQuality.PngCompressionLevel = savedExportQuality.PngCompressionLevel;
            ExportQuality.JpgQuality = savedExportQuality.JpgQuality;
            ExportQuality.DefaultExportFormat = ImageFormats.Find(ImageFormats.Manual, savedExportQuality.DefaultExportFormat).Id;
        }

        var savedSilentExport = SilentExportSettingsService.Load();
        if (savedSilentExport is not null)
        {
            SilentExport.Enabled = savedSilentExport.Enabled;
            SilentExport.Folder = savedSilentExport.Folder ?? "";
            SilentExport.Format = ImageFormats.Find(ImageFormats.Silent, savedSilentExport.Format).Id;
            SilentExport.NamePattern = string.IsNullOrWhiteSpace(savedSilentExport.NamePattern) ? SilentExportNaming.DefaultPattern : savedSilentExport.NamePattern;
            SilentExport.KeepJob = savedSilentExport.KeepJob;
        }

        var settings = AppSettingsService.Load();
        if (settings is null) return; // first run, or unreadable — keep the hardcoded defaults

        SplitRows = settings.SplitRows;
        SplitColumns = settings.SplitColumns;
        OverlapPixels = settings.OverlapPixels;

        if (!string.IsNullOrEmpty(settings.SelectedPrinter) && PrinterNames.Contains(settings.SelectedPrinter))
            SelectedPrinter = settings.SelectedPrinter; // triggers RefreshPaperSizes for THIS printer

        if (!string.IsNullOrEmpty(settings.SelectedPaperSizeName))
        {
            var match = PaperSizes.FirstOrDefault(p => string.Equals(p.Name, settings.SelectedPaperSizeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                SelectedPaperSizeInfo = match;
        }

        IsLandscape = settings.IsLandscape;
        ScalePercent = settings.ScalePercent;
        FitToPage = settings.FitToPage;
        MarginLeftInches = settings.MarginLeftInches;
        MarginTopInches = settings.MarginTopInches;
        MarginRightInches = settings.MarginRightInches;
        MarginBottomInches = settings.MarginBottomInches;
        ShowPageNumbers = settings.ShowPageNumbers;
        if (PageNumberPositions.Contains(settings.SelectedPageNumberPosition))
            SelectedPageNumberPosition = settings.SelectedPageNumberPosition;
        PrintWholePage = settings.PrintWholePage;
        DefaultPresetName = settings.DefaultPresetName;
        ZoomWheelSensitivity = settings.ZoomWheelSensitivity;
        EffectsDebounceMilliseconds = settings.EffectsDebounceMilliseconds;
        LinkExportsToJobs = settings.LinkExportsToJobs;
        UiScale = settings.UiScale;
        DialogScale = settings.DialogScale;
        IconLightness = settings.IconLightness;
        ThemeName = CleanPrint.Core.Theming.ThemeCatalog.Resolve(settings.Theme, settings.DarkMode);
        JobCacheLimitCountEnabled = settings.JobCacheLimitCountEnabled;
        JobCacheMaxJobs = settings.JobCacheMaxJobs;
        JobCacheLimitSizeEnabled = settings.JobCacheLimitSizeEnabled;
        JobCacheMaxSizeMb = settings.JobCacheMaxSizeMb;
        JobCacheShowWarning = settings.JobCacheShowWarning;

        // Default polygon style for the NEXT new polygon — was always hardcoded to blue/3px/
        // stroke-only/50% opacity regardless of whatever was last actually used.
        _defaultPolygonColor = HexToColor(settings.DefaultStrokeColorHex);
        _defaultPolygonThickness = settings.DefaultStrokeThickness;
        _defaultHasStroke = settings.DefaultHasStroke;
        _defaultFillColor = string.IsNullOrEmpty(settings.DefaultFillColorHex) ? null : HexToColor(settings.DefaultFillColorHex);
        _defaultFillOpacity = settings.DefaultFillOpacity;
    }

    /// <summary>Captures the split &amp; print controls' current values for AppSettingsService to
    /// persist — called from MainWindow's Closing handler, the same point WindowPlacementService's
    /// save already happens at.</summary>
    public AppSettings BuildSettingsSnapshot() => new()
    {
        SplitRows = SplitRows,
        SplitColumns = SplitColumns,
        OverlapPixels = OverlapPixels,
        SelectedPrinter = SelectedPrinter,
        SelectedPaperSizeName = SelectedPaperSize,
        IsLandscape = IsLandscape,
        ScalePercent = ScalePercent,
        FitToPage = FitToPage,
        MarginLeftInches = MarginLeftInches,
        MarginTopInches = MarginTopInches,
        MarginRightInches = MarginRightInches,
        MarginBottomInches = MarginBottomInches,
        ShowPageNumbers = ShowPageNumbers,
        SelectedPageNumberPosition = SelectedPageNumberPosition,
        PrintWholePage = PrintWholePage,
        DefaultPresetName = DefaultPresetName,
        ZoomWheelSensitivity = ZoomWheelSensitivity,
        EffectsDebounceMilliseconds = EffectsDebounceMilliseconds,
        LinkExportsToJobs = LinkExportsToJobs,
        UiScale = UiScale,
        DialogScale = DialogScale,
        IconLightness = IconLightness,
        Theme = ThemeName,
        DarkMode = CleanPrint.Core.Theming.ThemeCatalog.Find(ThemeName).IsDark,
        JobCacheLimitCountEnabled = JobCacheLimitCountEnabled,
        JobCacheMaxJobs = JobCacheMaxJobs,
        JobCacheLimitSizeEnabled = JobCacheLimitSizeEnabled,
        JobCacheMaxSizeMb = JobCacheMaxSizeMb,
        JobCacheShowWarning = JobCacheShowWarning,
        DefaultStrokeColorHex = $"#{_defaultPolygonColor.R:X2}{_defaultPolygonColor.G:X2}{_defaultPolygonColor.B:X2}",
        DefaultStrokeThickness = _defaultPolygonThickness,
        DefaultHasStroke = _defaultHasStroke,
        DefaultFillColorHex = _defaultFillColor is { } fc ? $"#{fc.R:X2}{fc.G:X2}{fc.B:X2}" : null,
        DefaultFillOpacity = _defaultFillOpacity,
    };

    // ---- Job / page loading ----

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaiseAllCanExecuteChanged();
        }
    }

    private void RaiseAllCanExecuteChanged()
    {
        NextPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        FirstPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
        ApplySplitCommand.RaiseCanExecuteChanged();
        ApplySplitAllPagesCommand.RaiseCanExecuteChanged();
        PrintAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Loads every page_*.png in the given job folder, in order, and displays the first one.</summary>
    public async void LoadJob(string jobFolder)
    {
        if (!Directory.Exists(jobFolder))
        {
            StatusText = $"Job folder not found: {jobFolder}";
            return;
        }

        var files = CleanPrintPaths.GetJobPageFiles(jobFolder).ToList();
        if (files.Count == 0)
        {
            StatusText = $"No pages found in job folder: {jobFolder}";
            return;
        }

        // A private job being left is deleted once the new job is current (below) — that is what makes it private.
        string? leavingPrivateJobId = CleanPrintPaths.IsPrivateJobId(_jobId) ? _jobId : null;

        // Persist whatever the OUTGOING job's effect settings/zoom currently are before switching
        // away from it — otherwise adjustments made just before switching jobs (with the 300ms
        // debounce not yet having fired) would be lost. Cancel any pending debounced save too —
        // otherwise it could fire AFTER _jobId below has already changed to the NEW job, writing
        // the OLD job's zoom into the wrong job-state.json.
        _zoomSaveDebounceCts?.Cancel();
        SaveCurrentJobState();

        _pageFilePaths.Clear();
        _pageFilePaths.AddRange(files);
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageCountDisplay));
        OnPropertyChanged(nameof(CurrentPageNumber));
        // New job: the Print dialog's preview starts over at page 1 and drops the old job's image
        // (its next activation re-renders). Bumping the token also discards any render still in
        // flight for the previous job.
        _printPreviewRenderToken++;
        IsPrintPreviewRendering = false; // the superseded render's own finally-block won't clear it (its token is stale now)
        PrintPreviewImage = null;
        ResetPrintPreviewPaging();
        _ = RefreshJobCacheUsageAsync(); // a job was just created/opened — the cache may have grown
        _jobId = Path.GetFileName(jobFolder);
        OnPropertyChanged(nameof(CurrentJobId)); // lets MainWindow refresh Previous/Next Job button state for whichever job just loaded
        RaisePrivateStateChanged();
        if (leavingPrivateJobId is not null && !string.Equals(leavingPrivateJobId, _jobId, StringComparison.OrdinalIgnoreCase))
            PrivateJobService.DeleteFolder(CleanPrintPaths.JobFolder(leavingPrivateJobId));
        _pageOverrides.Clear(); // overrides are keyed by index within THIS job; a new job starts fresh
        _pagePrintOverrides.Clear();
        _imageCache.Clear();    // pictures are per job
        _jobAnnotationSettings = await _polygonStore.LoadAsync(_jobId);

        // Null for jobs that don't need an override (see JobCaptureInfo's own doc comment) — every
        // PngPageLoader.Load call below for this job passes this same value.
        _capturedDpiOverride = JobCaptureInfoService.Load(jobFolder)?.Dpi;

        // Null for every job except one made with New Blank Job — which is what switches the page controls (add / insert / delete) on.
        _blankJobInfo = BlankJobInfoService.Load(jobFolder);
        OnPropertyChanged(nameof(IsBlankJob));
        OnPropertyChanged(nameof(CanDeletePage));

        _effectSettingsDebounceCts?.Cancel(); // belt-and-braces: also cancel anything already pending from before this call
        var existingState = JobViewStateService.Load(jobFolder);
        _suppressEffectReprocessOnChange = true;
        try
        {
            if (existingState is not null)
            {
                // This job has been opened (and worked on) before — restore exactly how it was
                // left, rather than re-deriving from the default-preset logic (which only governs
                // a job's very first-ever load).
                if (existingState.EffectSettings is not null)
                    EffectSettings.CopyFrom(existingState.EffectSettings);
                Zoom = existingState.Zoom; // proper setter, not the backing field — the zoom slider's binding needs the PropertyChanged notification this raises
            }
            else
            {
                // New job: apply the configured default preset (General settings), or reset to "no
                // effect at all" if none is configured — a freshly-opened job starts predictably
                // rather than silently carrying over whatever effect settings a previous job
                // happened to leave active (the original complaint that led to the whole Effects
                // feature: background whitening being always-on and unavoidable).
                var preset = !string.IsNullOrEmpty(DefaultPresetName) ? EffectPresetService.Load(DefaultPresetName) : null;
                EffectSettings.CopyFrom(preset ?? ImageEffectSettings.NoEffects());
            }
        }
        finally
        {
            _suppressEffectReprocessOnChange = false;
        }

        await LoadPageAsync(0);
    }

    // =====================================================================================================================
    // Pages of a blank job: add, insert, delete
    // =====================================================================================================================
    // Only a job made with New Blank Job (it has a blank-job.json) offers these. A captured or opened job's pages are what it was made from; a blank
    // job's pages are ours to make and remove. A page is a file (page_0007.png), and these things are keyed by the page's NUMBER: its annotations, its
    // per-page split override, its undo history. So adding or deleting a page renames the files AND moves each of those to the same new number, using the
    // tested rules in PageRenumbering — otherwise page 3's shapes would end up on whatever page is now called 3.

    private BlankJobInfo? _blankJobInfo;

    /// <summary>True for a job made with New Blank Job: its pages can be added to and removed.</summary>
    public bool IsBlankJob => _blankJobInfo is not null;

    /// <summary>A blank job with more than one page: a job always keeps at least one.</summary>
    public bool CanDeletePage => IsBlankJob && PageCount > 1;

    /// <summary>Adds a white page at the job's own page size: right after the page on screen, or at the end of the job. Then shows it.</summary>
    public async Task AddBlankPageAsync(bool afterCurrent)
    {
        if (_blankJobInfo is not { } info || string.IsNullOrEmpty(_jobId) || IsBusy) return;

        string jobFolder = CleanPrintPaths.JobFolder(_jobId);
        int count = _pageFilePaths.Count;
        int newNumber = afterCurrent ? CurrentPageIndex + 2 : count + 1;

        IsBusy = true;
        bool done = false;
        try
        {
            // What is on screen goes to disk under the number it has NOW, before any number changes.
            await SavePolygonsForCurrentPageAsync();

            await Task.Run(() =>
            {
                foreach (var (from, to) in PageRenumbering.PlanInsertRenames(count, newNumber))
                    File.Move(Path.Combine(jobFolder, PageRenumbering.FileName(from)), Path.Combine(jobFolder, PageRenumbering.FileName(to)));
                BlankJobService.WriteBlankPage(Path.Combine(jobFolder, PageRenumbering.FileName(newNumber)), info);
            });
            await ReKeyPagesAsync(page => PageRenumbering.AfterInsert(page, newNumber));
            done = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not add the page: {ex.Message}";
        }
        finally
        {
            RefreshPageListFromDisk(jobFolder);
            IsBusy = false;
        }

        // On success show the new page; after a failure just reload where we were, from what is on disk now.
        await LoadPageAsync(done ? newNumber - 1 : Math.Min(CurrentPageIndex, _pageFilePaths.Count - 1));
        if (done) StatusText = $"Added page {newNumber} ({info.PaperName}, {info.Dpi} DPI) — now {_pageFilePaths.Count} pages.";
    }

    /// <summary>Deletes the page on screen, for good (the caller asks first). The pages after it move down one, with everything that belongs to them.</summary>
    public async Task DeleteCurrentPageAsync()
    {
        if (_blankJobInfo is null || string.IsNullOrEmpty(_jobId) || IsBusy) return;

        int count = _pageFilePaths.Count;
        if (count <= 1)
        {
            StatusText = "A job needs at least one page, so its only page can't be deleted.";
            return;
        }

        string jobFolder = CleanPrintPaths.JobFolder(_jobId);
        int index = CurrentPageIndex;
        int deletedNumber = index + 1;
        string deletedFile = _pageFilePaths[index];

        IsBusy = true;
        bool done = false;
        try
        {
            await Task.Run(() =>
            {
                File.Delete(deletedFile);
                foreach (var (from, to) in PageRenumbering.PlanDeleteRenames(count, deletedNumber))
                    File.Move(Path.Combine(jobFolder, PageRenumbering.FileName(from)), Path.Combine(jobFolder, PageRenumbering.FileName(to)));
            });
            await ReKeyPagesAsync(page => PageRenumbering.AfterDelete(page, deletedNumber));
            done = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not delete the page: {ex.Message}";
        }
        finally
        {
            RefreshPageListFromDisk(jobFolder);
            IsBusy = false;
        }

        await LoadPageAsync(done ? PageRenumbering.IndexToShowAfterDelete(index, count) : Math.Min(index, _pageFilePaths.Count - 1));
        if (done) StatusText = $"Deleted page {deletedNumber} — now {_pageFilePaths.Count} pages.";
    }

    /// <summary>Moves everything keyed by page number to its new number (a page that maps to null — the deleted one — is dropped), and saves the annotations.</summary>
    private async Task ReKeyPagesAsync(Func<int, int?> newNumberOf)
    {
        _jobAnnotationSettings.Pages = PageRenumbering.Remap(_jobAnnotationSettings.Pages, newNumberOf);

        // The split overrides are keyed by page INDEX (number - 1).
        var overrides = _pageOverrides.ToList();
        _pageOverrides.Clear();
        foreach (var (index, value) in overrides)
            if (newNumberOf(index + 1) is int moved) _pageOverrides[moved - 1] = value;

        // The Print dialog's per-page settings, keyed by page INDEX like the split overrides.
        var printOverrides = _pagePrintOverrides.ToList();
        _pagePrintOverrides.Clear();
        foreach (var (index, value) in printOverrides)
            if (newNumberOf(index + 1) is int moved) _pagePrintOverrides[moved - 1] = value;

        // Undo history is keyed "jobId|pageNumber|variant" (folder names can't contain '|', so splitting on it is safe). Another job's histories are left as they are.
        var histories = _histories.ToList();
        _histories.Clear();
        foreach (var (key, history) in histories)
        {
            var parts = key.Split('|');
            if (parts.Length == 3 && parts[0] == _jobId && int.TryParse(parts[1], out int page))
            {
                if (newNumberOf(page) is int moved) _histories[$"{parts[0]}|{moved}|{parts[2]}"] = history;
            }
            else _histories[key] = history;
        }

        await _polygonStore.SaveAsync(_jobId, _jobAnnotationSettings);
    }

    /// <summary>Re-reads the job folder's page files into the page list, and tells everything that shows or depends on the list.</summary>
    private void RefreshPageListFromDisk(string jobFolder)
    {
        _pageFilePaths.Clear();
        _pageFilePaths.AddRange(CleanPrintPaths.GetJobPageFiles(jobFolder));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageCountDisplay));
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(CanDeletePage));
        RaiseAllCanExecuteChanged();

        // The Print dialog's preview was of the old page list: start it over, and discard any render still in flight (as LoadJob does).
        _printPreviewRenderToken++;
        IsPrintPreviewRendering = false;
        PrintPreviewImage = null;
        ResetPrintPreviewPaging();
        _ = RefreshJobCacheUsageAsync();
    }

    private async Task ChangePageAsync(int delta)
    {
        int newIndex = CurrentPageIndex + delta;
        if (newIndex < 0 || newIndex >= _pageFilePaths.Count) return;
        await LoadPageAsync(newIndex);
    }

    /// <summary>
    /// Rotates the current page's SOURCE FILE on disk 90 degrees (clockwise or counterclockwise),
    /// then reloads it — everything downstream (whitening, crop, preview) rebuilds from the newly-
    /// rotated source, exactly as if the page had always been captured in that orientation.
    ///
    /// Uses WPF's own imaging APIs (BitmapDecoder + TransformedBitmap + PngBitmapEncoder) rather
    /// than OpenCvSharp for this specifically: OpenCvSharp's Cv2.ImWrite doesn't preserve a PNG's
    /// DPI metadata, and PngPageLoader.Load reads a page's DPI FROM that metadata (frame.DpiX) — so
    /// rotating via OpenCvSharp would have silently reset the page's reported DPI to some default
    /// on the very next load, which would then be wrong for whitening's kernel size, print/export
    /// scale, and polygon stroke-thickness scaling. TransformedBitmap inherits DpiX/DpiY from its
    /// source unchanged, so re-encoding it preserves the original DPI correctly. A 90-degree
    /// rotation is also an exact, pixel-aligned transform (transposing rows/columns, no fractional
    /// shifts), so this is lossless — no interpolation/resampling artifacts either.
    ///
    /// Clears any saved polygons for this page (both Before/After variants) first: their vertex
    /// coordinates were established relative to the PREVIOUS orientation, so keeping them would
    /// leave them silently misaligned against the newly-rotated image rather than erroring
    /// visibly — the caller (MainWindow.xaml.cs) confirms this with the person before calling here.
    /// </summary>
    public async Task RotatePageAsync(bool clockwise)
    {
        if (CurrentPageIndex < 0 || CurrentPageIndex >= _pageFilePaths.Count) return;

        string filePath = _pageFilePaths[CurrentPageIndex];

        await Task.Run(() =>
        {
            BitmapSource original;
            using (var stream = File.OpenRead(filePath))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                original = decoder.Frames[0];
                original.Freeze(); // OnLoad already fully decoded the pixels, so this is safe once the stream below closes
            }

            var rotated = new TransformedBitmap(original, new RotateTransform(clockwise ? 90 : -90));
            rotated.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rotated));

            // Write to a temp file first, then swap in — avoids corrupting the original page file
            // if something goes wrong partway through the write (e.g. disk full).
            string tempPath = filePath + ".rotating.tmp";
            using (var outStream = File.Create(tempPath))
                encoder.Save(outStream);
            File.Delete(filePath);
            File.Move(tempPath, filePath);
        });

        int pageNumber = CurrentPageIndex + 1;
        if (_jobAnnotationSettings.Pages.Remove(pageNumber))
            await _polygonStore.SaveAsync(_jobId, _jobAnnotationSettings);

        await LoadPageAsync(CurrentPageIndex);
    }

    // =====================================================================================================================
    // Edit base image
    // =====================================================================================================================
    // Turn / crop / apply effects to the current page's own image FILE, for good (the editor is BaseImageEditorWindow; the pixel work is BaseImageEditService; the
    // geometry is BaseImageEditMath and AnnotationEditTransform in Core, tested). The page is the only thing that changes on disk; what has to follow it is everything
    // positioned on it — so after the new image is written, every shape, text and picture on the page is carried to where its part of the page went (never deleted, even if the
    // crop now leaves it off the page), and the page's undo history is dropped, since its saved states are in the old coordinates.

    /// <summary>What the editor needs, or null (with a message) if there is nothing to edit right now.</summary>
    public BaseImageEditContext? GetBaseImageEditContext()
    {
        if (IsBusy || _currentRawPage is null || string.IsNullOrEmpty(_jobId))
        {
            StatusText = "Load a page first (and wait for it to finish loading) to edit its base image.";
            return null;
        }
        if (IsDrawingPolygon)
        {
            StatusText = "Finish or cancel what you are drawing, then edit the base image.";
            return null;
        }
        return new BaseImageEditContext(_currentRawPage, _imageProcessing, CurrentPageIndex + 1, _pageFilePaths.Count);
    }

    /// <summary>Where the auto-cropped view of a page starts on the whole image, in image pixels — (0,0) when auto-crop is off or there is nothing to trim. Annotations in the
    /// "After" view are measured from that corner (see DrawIncludedPolygonsOntoPage's notes), so an edit has to know it before and after. Worked out exactly as LoadPageAsync does.</summary>
    private async Task<GeoPoint> AutoCropOriginAsync(CapturedPage page, ImageEffectSettings settings)
    {
        if (!settings.AutoCropEnabled) return new GeoPoint(0, 0);
        var effected = await _imageProcessing.ApplyEffectsAsync(page, settings);
        var rect = await _imageProcessing.FindContentCropAsync(effected, settings.AutoCropWhiteThreshold, settings.AutoCropPaddingPixels);
        return rect is { } r ? new GeoPoint(r.X, r.Y) : new GeoPoint(0, 0);
    }

    /// <summary>Saves the editor's result over the current page's image and moves everything on the page to follow. Returns false (page and annotations untouched) if the
    /// new image could not be written.</summary>
    public async Task<bool> ApplyBaseImageEditAsync(BaseImageEditResult result)
    {
        if (_currentRawPage is null || string.IsNullOrEmpty(_jobId) || IsBusy) return false;

        // Nothing turned, nothing cropped, no effects: leave the file alone (re-encoding an unchanged JPEG would only lose quality).
        if (Math.Abs(result.AngleDegrees) < 1e-9 && !result.EffectsBaked && result.Skew is null && !result.HasErasures &&
            result.Crop == new PixelRect(0, 0, _currentRawPage.Width, _currentRawPage.Height))
        {
            StatusText = "No changes to save.";
            return true;
        }

        int index = CurrentPageIndex, pageNumber = index + 1;
        string pageFile = _pageFilePaths[index];
        string jobFolder = CurrentJobFolder;
        var oldPage = _currentRawPage;

        IsBusy = true;
        try
        {
            StatusText = "Saving the edited image…";

            // What is on screen goes into the saved annotations FIRST, so that everything is carried across — not just what was saved before the last edit.
            await SavePolygonsForCurrentPageAsync();

            // Where the old "After" view started on the whole image (needs the old image and the effects it was shown with, so it comes before either changes).
            var oldOrigin = await AutoCropOriginAsync(oldPage, EffectSettings);

            // The new image goes over the old. If this fails, nothing else has been touched.
            await Task.Run(() => BaseImageEditService.WritePage(pageFile, result.NewPage, ExportQuality.JpgQuality));

            // Effects that changed the pixels are now IN the image, so the settings that described how to show the OLD image no longer apply: start the job's effects over
            // exactly as a newly opened job does (the default preset, or none). If the edit only turned and cropped, the settings are left alone.
            if (result.EffectsBaked)
            {
                var preset = !string.IsNullOrEmpty(DefaultPresetName) ? EffectPresetService.Load(DefaultPresetName) : null;
                _effectSettingsDebounceCts?.Cancel();
                _suppressEffectReprocessOnChange = true;
                try { EffectSettings.CopyFrom(preset ?? ImageEffectSettings.NoEffects()); }
                finally { _suppressEffectReprocessOnChange = false; }
                SaveCurrentJobState();
            }

            var newOrigin = await AutoCropOriginAsync(result.NewPage, EffectSettings);

            // Move everything on the page — in both views. The Before view is measured from the whole image; the After view from the auto-cropped one.
            if (_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants))
            {
                foreach (var (variant, annotations) in variants)
                {
                    bool cropped = variant == ImageVariant.Cleaned;
                    var transform = new AnnotationEditTransform(oldPage.Width, oldPage.Height, result.AngleDegrees, result.Crop, oldPage.Dpi,
                        cropped ? oldOrigin : new GeoPoint(0, 0), cropped ? newOrigin : new GeoPoint(0, 0), result.Skew);
                    foreach (var annotation in annotations.Polygons)
                        transform.ApplyTo(annotation);
                }
            }

            // The page's undo history is in the old coordinates: stepping back into it would put things where they no longer belong.
            foreach (var key in _histories.Keys.Where(k => k.StartsWith($"{_jobId}|{pageNumber}|", StringComparison.Ordinal)).ToList())
                _histories.Remove(key);

            await _polygonStore.SaveAsync(_jobId, _jobAnnotationSettings);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save the edited image: {ex.Message}";
            IsBusy = false;
            await LoadPageAsync(index); // show whatever is on disk now
            return false;
        }

        IsBusy = false;
        RefreshPageListFromDisk(jobFolder); // the Print dialog's preview was of the old image: start it over
        await LoadPageAsync(index);
        StatusText = $"Base image updated: {result.NewPage.Width:N0} × {result.NewPage.Height:N0} pixels" +
                     (result.EffectsBaked ? " — the effects are now part of the image, so the effect settings were reset." : ".");
        return true;
    }

    private async Task LoadPageAsync(int index)
    {
        IsBusy = true;
        try
        {
            CurrentPageIndex = index;
            StatusText = $"Loading page {index + 1} of {_pageFilePaths.Count}...";

            // Decoding + whitening off the UI thread; PngPageLoader does file IO + pixel conversion.
            _currentRawPage = await Task.Run(() => PngPageLoader.Load(_pageFilePaths[index], index + 1, _jobId, _capturedDpiOverride));
            _currentWhitenedPage = await _imageProcessing.ApplyEffectsAsync(_currentRawPage, EffectSettings);
            if (EffectSettings.AutoCropEnabled)
                _currentWhitenedPage = await _imageProcessing.CropToContentAsync(_currentWhitenedPage, EffectSettings.AutoCropWhiteThreshold, EffectSettings.AutoCropPaddingPixels);

            // A page change invalidates any split done on the previous page.
            _currentSplitTiles = Array.Empty<SplitTile>();
            _currentSplitTileLabels = Array.Empty<string>();
            SplitPreviewTiles.Clear();

            // Order matters: original dimensions (independent of any hiding) must be known before
            // polygons load (their own Freeze/Hide validation checks against these), polygons must
            // be loaded and hidden ranges recomputed before the preview image is built (so it can
            // actually remove the right bands), — see UpdateOriginalPageDimensions/
            // RefreshPolygonsForCurrentPageAndVariant/RefreshPreviewImage's docs.
            UpdateOriginalPageDimensions();
            RefreshPolygonsForCurrentPageAndVariant();
            RefreshPreviewImage();
            StatusText = _jobId; // the title bar then reads "Clean Print - <job>": the page number is in the navigation strip below, so isn't repeated up here

            // Show whatever settings apply to THIS page: its own override if it has one,
            // otherwise the global defaults — without writing back into _pageOverrides as a
            // side effect of just navigating here.
            _suppressOverrideWrite = true;
            if (_pageOverrides.TryGetValue(index, out var pageOverride))
            {
                _useOverrideForCurrentPage = true;
                _overrideRows = pageOverride.Rows;
                _overrideColumns = pageOverride.Columns;
                _overrideOverlapPixels = pageOverride.OverlapPixels;
            }
            else
            {
                _useOverrideForCurrentPage = false;
                _overrideRows = SplitRows;
                _overrideColumns = SplitColumns;
                _overrideOverlapPixels = OverlapPixels;
            }
            OnPropertyChanged(nameof(UseOverrideForCurrentPage));
            OnPropertyChanged(nameof(OverrideRows));
            OnPropertyChanged(nameof(OverrideColumns));
            OnPropertyChanged(nameof(OverrideOverlapPixels));
            _suppressOverrideWrite = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Preview state ----

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetField(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(ZoomedPreviewWidth));
                OnPropertyChanged(nameof(ZoomedPreviewHeight));
                OnPropertyChanged(nameof(FreezeEditCanvasWidth));
                OnPropertyChanged(nameof(FreezeEditCanvasHeight));
            }
        }
    }

    private double _zoom = 1.0;
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetField(ref _zoom, Math.Clamp(value, 0.1, 8.0)))
            {
                OnPropertyChanged(nameof(ZoomedPreviewWidth));
                OnPropertyChanged(nameof(ZoomedPreviewHeight));
                OnPropertyChanged(nameof(FreezeCanvasScaledPadding));
                OnPropertyChanged(nameof(FreezeCanvasNegativePadding));
                OnPropertyChanged(nameof(FreezeEditCanvasWidth));
                OnPropertyChanged(nameof(FreezeEditCanvasHeight));
                RefreshAllPolygonDisplays();
                DebounceSaveZoom();
            }
        }
    }

    /// <summary>The image's actual on-screen (zoomed) size — used to size the polygon overlay so
    /// it covers exactly the rendered image regardless of zoom level. See RefreshPolygonDisplay
    /// for why the overlay is scaled manually instead of inheriting zoom via a shared
    /// LayoutTransform.</summary>
    public double ZoomedPreviewWidth => (PreviewImage?.Width ?? 0) * Zoom;
    public double ZoomedPreviewHeight => (PreviewImage?.Height ?? 0) * Zoom;

    /// <summary>
    /// How far beyond the image's edges a polygon vertex can be placed and still be
    /// clickable/draggable, in UNSCALED image-space units. Points genuinely outside the page are a
    /// legitimate, intentional use case (e.g. a boundary that needs to run past the content's
    /// edge) — without this margin, FreezeEditCanvas (the hit-test surface) would only ever cover
    /// the image itself, so any point placed outside it could be seen but never touched again.
    /// MainWindow.xaml.cs's GetImageSpacePosition must subtract this same constant.
    /// </summary>
    public const double FreezeCanvasPaddingImageSpace = 2000.0;

    public double FreezeCanvasScaledPadding => FreezeCanvasPaddingImageSpace * Zoom;
    public double FreezeCanvasNegativePadding => -FreezeCanvasScaledPadding;
    public double FreezeEditCanvasWidth => ZoomedPreviewWidth + 2 * FreezeCanvasScaledPadding;
    public double FreezeEditCanvasHeight => ZoomedPreviewHeight + 2 * FreezeCanvasScaledPadding;

    // Freeze Row/Column pinned-overlay offsets now live per-polygon on PolygonViewModel (see its
    // FrozenOverlayOffsetX/Y docs) rather than as single shared values here, since Row and Column
    // freezes pin along different axes and can be active simultaneously.

    private bool _showBefore;
    public bool ShowBefore
    {
        get => _showBefore;
        set
        {
            if (SetField(ref _showBefore, value))
            {
                // Same ordering requirement as LoadPageAsync — see UpdateOriginalPageDimensions'
                // docs for why.
                UpdateOriginalPageDimensions();
                RefreshPolygonsForCurrentPageAndVariant(); // raw/cleaned have separate polygons
                RefreshPreviewImage();
            }
        }
    }

    // ---- Polygons (ver_0.1.1: general-purpose multi-polygon drawing engine. FreezeRow and
    // HideRow/HideColumn are both fully implemented, including HideRow/HideColumn's gap-closing.) ----

    /// <summary>
    /// The page's TRUE, un-hidden dimensions (device-independent units), independent of
    /// PreviewImage — which, once any Hide Row/Column is active, becomes the SMALLER, recomposited
    /// (gap-closed) bitmap. All polygon Vertices are stored in this ORIGINAL space (so hiding/
    /// unhiding never touches other polygons' actual saved data), and Freeze Row/Hide Row/Hide
    /// Column validation always checks against this — not PreviewImage's current (possibly
    /// already-shrunk) size — since that's the space a polygon's own vertices are drawn in
    /// regardless of what else happens to be hidden elsewhere on the page.
    /// </summary>
    private double _originalPageWidth;
    private double _originalPageHeight;

    /// <summary>Merged, sorted, non-overlapping hidden Y-ranges (row-wise) and X-ranges
    /// (column-wise), in ORIGINAL image-space — derived from every closed HideRow/HideColumn
    /// polygon's bounding box. Recomputed by RecomputeHiddenBands whenever a polygon's Purpose or
    /// Vertices could have changed what's hidden.</summary>
    private List<(double Start, double End)> _hiddenYRanges = new();
    private List<(double Start, double End)> _hiddenXRanges = new();

    /// <summary>
    /// Rebuilds _hiddenYRanges/_hiddenXRanges from the CURRENT Polygons collection. Must run
    /// AFTER all of the current page+variant's polygons are loaded (or after any purpose/vertex/
    /// delete change), and BEFORE RefreshPreviewImage (which uses these ranges to actually remove
    /// the hidden bands from the displayed bitmap) and RefreshAllPolygonDisplays (which uses them
    /// to remap every polygon's on-screen position to match the now-shorter/narrower image).
    /// </summary>
    private void RecomputeHiddenBands()
    {
        var yRanges = new List<(double, double)>();
        var xRanges = new List<(double, double)>();

        foreach (var polygon in Polygons)
        {
            if (!polygon.IsClosed || polygon.Vertices.Count < 3) continue;

            if (polygon.Purpose == PolygonPurpose.HideRow)
                yRanges.Add((polygon.Vertices.Min(v => v.Y), polygon.Vertices.Max(v => v.Y)));
            else if (polygon.Purpose == PolygonPurpose.HideColumn)
                xRanges.Add((polygon.Vertices.Min(v => v.X), polygon.Vertices.Max(v => v.X)));
        }

        _hiddenYRanges = MergeRanges(yRanges);
        _hiddenXRanges = MergeRanges(xRanges);
    }

    private static List<(double Start, double End)> MergeRanges(List<(double Start, double End)> ranges)
    {
        if (ranges.Count == 0) return ranges;
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(double Start, double End)> { sorted[0] };
        foreach (var next in sorted.Skip(1))
        {
            var last = merged[^1];
            if (next.Start <= last.End) // overlapping or touching — combine
                merged[^1] = (last.Start, Math.Max(last.End, next.End));
            else
                merged.Add(next);
        }
        return merged;
    }

    /// <summary>Maps an ORIGINAL image-space coordinate to DISPLAY (post-hide, gap-closed) space:
    /// subtracts the total size of every hidden range entirely before it, and for a coordinate
    /// INSIDE a hidden range, collapses it to that range's start (since that content isn't shown
    /// at all). Ranges must be sorted and non-overlapping (as MergeRanges produces).</summary>
    private static double MapToDisplay(double original, List<(double Start, double End)> hiddenRanges)
    {
        double removedBefore = 0;
        foreach (var (start, end) in hiddenRanges)
        {
            if (original >= end) { removedBefore += end - start; continue; }
            if (original >= start) return start - removedBefore;
            break; // ranges are sorted — nothing further is relevant
        }
        return original - removedBefore;
    }

    /// <summary>Inverse of MapToDisplay: given a DISPLAY-space coordinate (e.g. from a mouse
    /// click), returns the corresponding ORIGINAL image-space coordinate — needed since every
    /// polygon's Vertices are always stored in original space regardless of what's currently
    /// hidden. A display coordinate exactly at a collapsed seam is ambiguous (many original values
    /// map there); this resolves it to the hidden range's start, which is rarely if ever an
    /// intentional click target in practice.</summary>
    private static double MapToOriginal(double display, List<(double Start, double End)> hiddenRanges)
    {
        double removedBefore = 0;
        foreach (var (start, end) in hiddenRanges)
        {
            double mappedStart = start - removedBefore;
            if (display < mappedStart) return display + removedBefore;
            removedBefore += end - start;
        }
        return display + removedBefore;
    }

    /// <summary>Converts a DISPLAY-space point (screen-derived, post-hide) into ORIGINAL image-space
    /// — used by MainWindow.xaml.cs's GetImageSpacePosition so clicks always resolve to the same
    /// coordinate system every polygon's Vertices are stored in.</summary>
    public Point MapDisplayToOriginal(double displayX, double displayY) =>
        new(MapToOriginal(displayX, _hiddenXRanges), MapToOriginal(displayY, _hiddenYRanges));

    /// <summary>
    /// Removes the given hidden ranges (in ORIGINAL image-space DIU) from a bitmap by copying the
    /// VISIBLE segments' raw pixel bytes into a new, smaller buffer with no gap between them —
    /// pure integer pixel/stride/byte-offset arithmetic throughout, deliberately avoiding WPF's
    /// DIU/DPI Rect-based drawing APIs (RenderTargetBitmap + DrawingVisual), since that category of
    /// conversion has been the source of most bugs in this project so far. isRowWise=true removes
    /// horizontal bands (shifts content up); false removes vertical bands (shifts content left).
    /// </summary>
    private static BitmapSource RemoveHiddenBands(BitmapSource source, List<(double Start, double End)> hiddenRangesDiu, bool isRowWise)
    {
        if (hiddenRangesDiu.Count == 0) return source;

        double pixelsPerDiu = source.DpiX / 96.0; // DpiX == DpiY throughout this project (uniform DPI)
        int totalPixels = isRowWise ? source.PixelHeight : source.PixelWidth;

        var pixelRanges = hiddenRangesDiu
            .Select(r => (Start: (int)Math.Round(r.Start * pixelsPerDiu), End: (int)Math.Round(r.End * pixelsPerDiu)))
            .Select(r => (Start: Math.Max(0, r.Start), End: Math.Min(totalPixels, r.End)))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start)
            .ToList();
        if (pixelRanges.Count == 0) return source;

        var visibleSegments = new List<(int Start, int Length)>();
        int cursor = 0;
        foreach (var (start, end) in pixelRanges)
        {
            if (start > cursor) visibleSegments.Add((cursor, start - cursor));
            cursor = Math.Max(cursor, end);
        }
        if (cursor < totalPixels) visibleSegments.Add((cursor, totalPixels - cursor));

        int newTotalPixels = visibleSegments.Sum(s => s.Length);
        if (newTotalPixels <= 0 || newTotalPixels == totalPixels) return source; // nothing left, or nothing actually removed — bail out safely

        int bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8; // 4 for Bgra32, used throughout this project
        int sourceStride = source.PixelWidth * bytesPerPixel;
        var fullBuffer = new byte[sourceStride * source.PixelHeight];
        source.CopyPixels(fullBuffer, sourceStride, 0);

        int newWidth = isRowWise ? source.PixelWidth : newTotalPixels;
        int newHeight = isRowWise ? newTotalPixels : source.PixelHeight;
        int newStride = newWidth * bytesPerPixel;
        var newBuffer = new byte[newStride * newHeight];

        int destOffsetPixels = 0;
        foreach (var (segStart, segLength) in visibleSegments)
        {
            if (isRowWise)
            {
                // Whole rows: one contiguous block copy per segment (width is unchanged).
                int srcByteOffset = segStart * sourceStride;
                int destByteOffset = destOffsetPixels * newStride;
                Array.Copy(fullBuffer, srcByteOffset, newBuffer, destByteOffset, segLength * sourceStride);
            }
            else
            {
                // Column ranges: row-by-row, since source and destination strides differ.
                for (int row = 0; row < source.PixelHeight; row++)
                {
                    int srcByteOffset = row * sourceStride + segStart * bytesPerPixel;
                    int destByteOffset = row * newStride + destOffsetPixels * bytesPerPixel;
                    Array.Copy(fullBuffer, srcByteOffset, newBuffer, destByteOffset, segLength * bytesPerPixel);
                }
            }
            destOffsetPixels += segLength;
        }

        var result = BitmapSource.Create(newWidth, newHeight, source.DpiX, source.DpiY, source.Format, source.Palette, newBuffer, newStride);
        result.Freeze();
        return result;
    }

    /// <summary>Every polygon on the current page + Before/After variant. A real ObservableCollection
    /// (not a replace-the-whole-list pattern) since individual polygons can be added/removed
    /// independently and the gallery/canvas should reflect that live.</summary>
    public ObservableCollectionSlim<PolygonViewModel> Polygons { get; } = new();

    /// <summary>Non-null only while actively drawing a BRAND NEW polygon (between NewPolygonCommand
    /// and Finish/Close/Cancel). Added to Polygons immediately so it renders live while being drawn.</summary>
    private PolygonViewModel? _drawingPolygon;
    public bool IsDrawingPolygon => _drawingPolygon is not null;

    /// <summary>True only while drawing a click-defined SHAPE: a rectangle, ellipse, circle, arc, or a text
    /// label waiting to be placed (see _drawingKind). Each is built from a fixed number of clicks (see
    /// ClicksNeeded) and finishes itself on the last one — unlike a polygon or curve, which keep taking
    /// points until Finish/Close. All of them are polygons under the hood, so they reuse _drawingPolygon
    /// and the normal polygon machinery (undo, persistence, right-click menu, editing, printing) rather
    /// than needing a parallel system.</summary>
    private bool _isDrawingShape;
    public bool IsDrawingShape => _isDrawingShape;

    /// <summary>Which shape the current shape-drawing session is making. ShapeKind.Polygon means a
    /// RECTANGLE here (a rectangle is a 4-vertex polygon, so it needs no kind of its own); the others are
    /// the parametric kinds. Meaningless while !IsDrawingShape.</summary>
    private ShapeKind _drawingKind = ShapeKind.Polygon;

    /// <summary>True while drawing an ellipse specifically — the only shape whose second click Ctrl
    /// (circle) and Shift (from the centre) change.</summary>
    public bool IsDrawingEllipse => _isDrawingShape && _drawingKind == ShapeKind.Ellipse;

    /// <summary>True while the text tool is waiting for its click: MainWindow prompts for the text at that
    /// click, opens the in-place editor, and commits through CommitInlineText.</summary>
    public bool IsPlacingText => _isDrawingShape && _drawingKind == ShapeKind.Text;

    /// <summary>The clicks made so far for the current shape (image units).</summary>
    private readonly List<Point> _shapePoints = new();

    /// <summary>Last cursor position seen during the shape rubber-band, so pressing/releasing Ctrl or
    /// Shift can redraw the preview immediately instead of waiting for the next mouse move.</summary>
    private Point? _lastShapeCursor;

    /// <summary>True only for the "drawing an open-ended polygon (or curve), click to add points" mode —
    /// used to hide the Finish/Close Polygon buttons during shape mode, where the clicks finish the
    /// shape automatically.</summary>
    public bool IsDrawingFreeformPolygon => IsDrawingPolygon && !IsDrawingShape;

    /// <summary>How many clicks a shape needs before it is finished.</summary>
    private static int ClicksNeeded(ShapeKind kind) => kind switch
    {
        ShapeKind.Circle3 or ShapeKind.Arc3 => 3,
        ShapeKind.Text => 1,
        _ => 2,
    };

    /// <summary>The one polygon whose vertex handles are currently shown for dragging/inserting/
    /// deleting: the polygon being freshly drawn, or — if nothing is being drawn — whichever
    /// existing polygon is the SOLE selected one (multi-select shows highlighting but not
    /// individually-editable handles, since simultaneous multi-polygon vertex editing isn't
    /// meaningful).</summary>
    public PolygonViewModel? EditablePolygon =>
        _drawingPolygon ?? (Polygons.Count(p => p.IsSelected) == 1 ? Polygons.First(p => p.IsSelected) : null);

    public bool IsEditingPolygon => EditablePolygon is not null;

    public bool CanCloseCurrentPolygon => IsDrawingFreeformPolygon && _drawingPolygon!.Vertices.Count >= 3;

    public RelayCommand NewPolygonCommand { get; }
    public RelayCommand NewRectangleCommand { get; }
    public RelayCommand NewEllipseCommand { get; }
    public RelayCommand NewCircleCenterCommand { get; }
    public RelayCommand NewCircle3Command { get; }
    public RelayCommand NewArcCommand { get; }
    public RelayCommand NewCurveCommand { get; }
    public RelayCommand NewTextCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand MirrorHorizontalCommand { get; }
    public RelayCommand MirrorVerticalCommand { get; }
    public RelayCommand FinishPolygonCommand { get; }
    public RelayCommand ClosePolygonCommand { get; }
    public RelayCommand CancelPolygonEditCommand { get; }
    public RelayCommand DeleteSelectedPolygonsCommand { get; }

    /// <summary>Undo history for the current EditablePolygon only — cleared whenever the editable
    /// polygon changes (new drawing session, or a different polygon becomes selected). Snapshots
    /// both Vertices and IsClosed together, since a deletion that drops a polygon below 3 vertices
    /// auto-opens it (see ApplyVertexList) — undoing that deletion needs to restore the CLOSED
    /// state too, not just the vertex it had back.</summary>
    private readonly Stack<(List<PolygonVertexViewModel> Vertices, bool IsClosed)> _polygonUndoStack = new();

    // ---- Line style (color/thickness), applied to the drawing/selected polygon(s) ----

    /// <summary>Session-level defaults, remembered so a change made via the combo boxes BEFORE
    /// starting a new polygon (i.e. while nothing is drawing/selected) still applies to the next
    /// one drawn — previously these choices had nothing to apply to and were silently dropped,
    /// with new polygons always starting from hardcoded defaults instead.</summary>
    private Color _defaultPolygonColor = Colors.Blue;
    private double _defaultPolygonThickness = 3.0;
    private bool _defaultHasStroke = true;

    /// <summary>Same idea as the stroke defaults above, for fill — a color/opacity chosen via the
    /// toolbar before drawing (or with nothing selected) now carries over to the next polygon
    /// created, instead of being silently discarded because there was no polygon to apply it to at
    /// the time.</summary>
    private Color? _defaultFillColor;
    private double _defaultFillOpacity = 50.0;

    /// <summary>The same 9-color palette used by both the stroke and fill combos, in the same
    /// order — kept as one shared source so the two never drift apart again. Returns fresh
    /// ColorOption instances each call since ObservableCollectionSlim needs its own objects, but
    /// ColorOption is effectively immutable (init-only properties), so sharing the underlying
    /// Color values is all that actually matters for consistency between the two combos.</summary>
    private static IEnumerable<ColorOption> BuildStandardColorPalette() => new[]
    {
        new ColorOption { Name = "Red", Color = Colors.Red },
        new ColorOption { Name = "Green", Color = Colors.Green },
        new ColorOption { Name = "Blue", Color = Colors.Blue },
        new ColorOption { Name = "Orange", Color = Colors.Orange },
        new ColorOption { Name = "Purple", Color = Colors.Purple },
        new ColorOption { Name = "Yellow", Color = Colors.Yellow },
        new ColorOption { Name = "Black", Color = Colors.Black },
        new ColorOption { Name = "Light Gray", Color = Colors.LightGray },
        new ColorOption { Name = "Dark Gray", Color = Colors.DarkGray },
        new ColorOption { Name = "White", Color = Colors.White },
        new ColorOption { Name = "Pink", Color = Colors.Pink },
        new ColorOption { Name = "Cyan", Color = Colors.Cyan },
    };

    public ObservableCollectionSlim<ColorOption> AvailableColors { get; } = new(BuildStandardColorPalette());

    private static readonly ColorOption NoStrokeOption = new() { Name = "No Stroke", Color = Colors.White, IsNoStroke = true };

    /// <summary>The list the stroke-color combo box should actually show right now — AvailableColors
    /// plus "No Stroke" ONLY when the current target is a closed, filled polygon (removing the
    /// outline only makes sense when the fill alone can still show where the shape is; on an
    /// unfilled polygon it would make the shape invisible entirely). Recomputed (via
    /// OnPropertyChanged) whenever the selection or fill state changes.</summary>
    public IEnumerable<ColorOption> AvailableStrokeColorOptions
    {
        get
        {
            var target = _drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected);
            // The SAME list instance every time for the same state. This used to build a fresh sequence on every read, so every
            // notification handed the combo a "new" list — which makes a ComboBox drop its selection and re-announce it, even when
            // nothing about the list had changed. Combined with notifying from inside the combo's own selection change, that is what
            // crashed choosing a stroke colour for text (see RaiseStyleControlsChangedAfterBinding).
            return target is { HasFill: true } ? _strokeOptionsWithNoStroke ??= AvailableColors.Append(NoStrokeOption).ToList() : AvailableColors;
        }
    }

    private List<ColorOption>? _strokeOptionsWithNoStroke;
    private List<ColorOption>? _fillOptionsWithoutNoFill;

    public ObservableCollectionSlim<double> AvailableThicknesses { get; } = new(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 });

    /// <summary>Reflects (and, when set, applies to) whichever polygon(s) the color combo box
    /// should currently affect: the polygon being drawn, or all currently-selected polygons — or,
    /// if neither, just the remembered default for the next polygon (so the combo is never blank).
    /// Selecting "No Stroke" (only offered when the target has a fill — see
    /// AvailableStrokeColorOptions) sets HasStroke=false instead of changing StrokeColor, so the
    /// original color is remembered if stroke is turned back on later.</summary>
    /// <summary>
    /// True only while the window re-asserts what the Color / Fill boxes should be SHOWING (MainWindow.SyncStyleCombos): the assignment it makes is a display correction, not a choice, so the setters must
    /// not treat it as one (it would re-apply colours and save). See that method for why the correction exists.
    /// </summary>
    public bool SuppressStyleWrites { get; set; }

    public ColorOption? CurrentPolygonColorOption
    {
        get
        {
            var target = _drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected);
            // "No Stroke" only if the list is actually offering it. A ComboBox handed a selection that isn't in its list shows NOTHING (and stays that way), so the answer
            // must always be one of the items on offer — whatever state the target is in.
            if (target is not null && !target.HasStroke && AvailableStrokeColorOptions.Contains(NoStrokeOption)) return NoStrokeOption;
            var color = target?.StrokeColor ?? _defaultPolygonColor;
            return AvailableColors.FirstOrDefault(c => c.Color == color) ?? AvailableColors.First();
        }
        set
        {
            if (value is null || SuppressStyleWrites) return;
            bool forText = StyleTargetsAreText;
            if (value.IsNoStroke)
            {
                // The outline only goes where a fill remains to show the thing (StyleRules). The list offers "No Stroke" on the strength of the FIRST selected item, but this
                // applies to every selected item — a text and a rectangle selected together would otherwise lose their only visible part and vanish.
                if (forText) { if (_textDefaults.HasFill) _textDefaults.HasStroke = false; }
                else if (_defaultFillColor is not null) _defaultHasStroke = false;
                foreach (var p in GetStyleTargets())
                    if (StyleRules.AllowStrokeOff(p.HasFill)) p.HasStroke = false;
            }
            else
            {
                if (forText) { _textDefaults.StrokeColor = value.Color; _textDefaults.HasStroke = true; }
                else { _defaultPolygonColor = value.Color; _defaultHasStroke = true; }
                foreach (var p in GetStyleTargets())
                {
                    p.StrokeColor = value.Color;
                    p.HasStroke = true;
                }
            }
            RefreshTextTargets();
            if (GetStyleTargets().Any())
                _ = SavePolygonsForCurrentPageAsync();
            RaiseStyleControlsChangedAfterBinding(); // stroke state changing affects whether "No Fill" is offered
        }
    }

    public double CurrentPolygonThickness
    {
        get => (_drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected))?.StrokeThickness ?? _defaultPolygonThickness;
        set
        {
            if (StyleTargetsAreText) _textDefaults.StrokeThickness = value; else _defaultPolygonThickness = value;
            foreach (var p in GetStyleTargets())
                p.StrokeThickness = value;
            RefreshTextTargets();
            if (GetStyleTargets().Any())
                _ = SavePolygonsForCurrentPageAsync();
            OnPropertyChanged(nameof(CurrentPolygonThickness));
        }
    }

    // ---- Fill color/opacity, only rendered while a polygon is closed (PolygonViewModel.HasFill) ----

    private static readonly ColorOption NoFillOption = new() { Name = "No Fill", Color = Colors.White, IsNoFill = true };

    public ObservableCollectionSlim<ColorOption> AvailableFillColors { get; } = new(
        new[] { NoFillOption }.Concat(BuildStandardColorPalette()));

    /// <summary>The list the fill-color combo box should actually show right now — AvailableFillColors,
    /// with "No Fill" removed when the current target already has no stroke either (the symmetric
    /// counterpart of AvailableStrokeColorOptions hiding "No Stroke" until a fill exists) — an
    /// entity with neither a stroke nor a fill would be entirely invisible, so at least one of the
    /// two "none" options is always withheld.</summary>
    public IEnumerable<ColorOption> AvailableFillColorOptions
    {
        get
        {
            var target = _drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected);
            return target is { HasStroke: false } ? _fillOptionsWithoutNoFill ??= AvailableFillColors.Where(c => !c.IsNoFill).ToList() : AvailableFillColors;
        }
    }

    /// <summary>Reflects (and, when set, applies to) whichever polygon(s) the fill combo box
    /// should currently affect — or, if none, the remembered default for the next polygon (see
    /// _defaultFillColor's docs for why that now exists).</summary>
    public ColorOption? CurrentPolygonFillColorOption
    {
        get
        {
            var target = _drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected);
            var fillColor = target?.FillColor ?? _defaultFillColor;
            var offered = AvailableFillColorOptions;
            // Same rule as the stroke combo: only ever answer with an item the list is offering ("No Fill" is withheld when there is no outline to keep the shape visible).
            ColorOption NoneOrFirst() => offered.Contains(NoFillOption) ? NoFillOption : offered.First();
            if (fillColor is null) return NoneOrFirst();
            return AvailableFillColors.FirstOrDefault(c => !c.IsNoFill && c.Color == fillColor.Value) ?? NoneOrFirst();
        }
        set
        {
            if (value is null || SuppressStyleWrites) return;
            Color? newFillColor = value.IsNoFill ? null : value.Color;
            // Likewise the fill only goes where an outline remains (StyleRules) — for the remembered defaults and for every selected item.
            if (StyleTargetsAreText) { if (newFillColor is not null || _textDefaults.HasStroke) _textDefaults.FillColor = newFillColor; }
            else if (newFillColor is not null || _defaultHasStroke) _defaultFillColor = newFillColor;
            foreach (var p in GetStyleTargets())
                if (newFillColor is not null || StyleRules.AllowFillOff(p.HasStroke)) p.FillColor = newFillColor;
            RefreshTextTargets();
            if (GetStyleTargets().Any())
                _ = SavePolygonsForCurrentPageAsync();
            RaiseStyleControlsChangedAfterBinding(); // fill state changing affects whether "No Stroke" is offered
        }
    }

    /// <summary>Fill opacity as a plain number (0-100), bound to the NumericUpDown control in the
    /// toolbar — or, with no current target, the remembered default for the next polygon (see
    /// _defaultFillOpacity's docs).</summary>
    public double CurrentPolygonFillOpacity
    {
        get => (_drawingPolygon ?? Polygons.FirstOrDefault(p => p.IsSelected))?.FillOpacity ?? _defaultFillOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (StyleTargetsAreText) _textDefaults.FillOpacity = clamped; else if (!StyleTargetsAreImages) _defaultFillOpacity = clamped;
            foreach (var p in GetStyleTargets())
                p.FillOpacity = clamped;
            RefreshTextTargets();
            if (GetStyleTargets().Any())
                _ = SavePolygonsForCurrentPageAsync();
            OnPropertyChanged(nameof(CurrentPolygonFillOpacity));
        }
    }

    /// <summary>Reflects (and, when set, applies to) whichever polygon(s) the "Include in Print"
    /// checkbox should currently affect. Unlike the other style properties, there's no remembered
    /// default for the next polygon — including a shape in the printed output is a deliberate,
    /// per-shape decision each time, not something that should silently carry over to the next one
    /// drawn.</summary>
    public bool CurrentPolygonIncludeInPrint
    {
        // Ticked when EVERYTHING it would act on is included; unticking (or ticking) then sets them all alike.
        get
        {
            var targets = IncludeInPrintTargets();
            return targets.Count > 0 && targets.All(p => p.IncludeInPrint);
        }
        set
        {
            var targets = IncludeInPrintTargets();
            foreach (var p in targets)
                p.IncludeInPrint = value;
            if (targets.Count > 0)
                _ = SavePolygonsForCurrentPageAsync();
            OnPropertyChanged(nameof(CurrentPolygonIncludeInPrint));
        }
    }

    /// <summary>
    /// Announces the Color / Thickness / Fill / Opacity / Include-in-Print controls — the ITEM LISTS first, then the selected values.
    /// The order matters: the stroke and fill combos are handed a new list whenever the selection changes what they may offer (for
    /// instance "No Fill" is withheld for something with no outline), and a ComboBox that is given a new list drops its selection.
    /// Announcing the selected value first and the list afterwards left the combo blank or stale — the Fill box not following the
    /// selection when clicking between a text and a shape. Announced the other way round, the value is read after the list is in place.
    /// </summary>
    /// <summary>
    /// For the style setters (Color and Fill), which run INSIDE the combo's own selection change: announces the controls once that
    /// has finished, rather than from within it. Handing a ComboBox a new item list while it is part-way through changing its
    /// selection — which is what announcing from the setter did to the stroke combo — is exactly the kind of re-entrancy WPF's
    /// selector does not tolerate. Posted at background priority so it runs after the binding update that called the setter.
    /// </summary>
    private void RaiseStyleControlsChangedAfterBinding()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(new Action(RaiseStyleControlsChanged), System.Windows.Threading.DispatcherPriority.Background);
        else
            RaiseStyleControlsChanged();
    }

    private void RaiseStyleControlsChanged()
    {
        OnPropertyChanged(nameof(AvailableStrokeColorOptions));
        OnPropertyChanged(nameof(AvailableFillColorOptions));
        OnPropertyChanged(nameof(CurrentPolygonColorOption));
        OnPropertyChanged(nameof(CurrentPolygonThickness));
        OnPropertyChanged(nameof(CurrentPolygonFillColorOption));
        OnPropertyChanged(nameof(CurrentPolygonFillOpacity));
        OnPropertyChanged(nameof(CurrentPolygonIncludeInPrint));
    }

    private IEnumerable<PolygonViewModel> GetStyleTargets() =>
        _drawingPolygon is not null ? new[] { _drawingPolygon } : Polygons.Where(p => p.IsSelected);

    /// <summary>True when the toolbar's style controls (Color, Thickness, Fill, Opacity) are currently acting on text only. A
    /// choice then belongs to the TEXT defaults (what the next text will look like), not to the shape defaults — otherwise
    /// setting some text's fill to black would also give the next rectangle a black fill.</summary>
    private bool StyleTargetsAreText
    {
        get
        {
            var targets = GetStyleTargets().ToList();
            return targets.Count > 0 && targets.All(p => p.IsText);
        }
    }

    /// <summary>True when the style controls are acting on pictures only. Opacity then belongs to the pictures, and must not become the remembered
    /// "default opacity for the next shape" the way a shape's does.</summary>
    private bool StyleTargetsAreImages
    {
        get
        {
            var targets = GetStyleTargets().ToList();
            return targets.Count > 0 && targets.All(p => p.IsImage);
        }
    }

    /// <summary>Text draws its outline from geometry laid out at the current zoom (UpdateTextDisplay), so a change to anything
    /// that affects the outline — thickness, whether there is one — has to re-lay the selected text out. (Colours and opacity
    /// are bound live and need nothing.)</summary>
    private void RefreshTextTargets()
    {
        foreach (var text in GetStyleTargets().Where(p => p.IsText))
            RefreshPolygonDisplay(text);
    }

    // ---- Drawing / editing ----

    /// <summary>Starts a brand-new polygon: creates it (using whatever color/thickness was last
    /// chosen via the combo boxes, even if nothing was selected/drawing at the time — see
    /// _defaultPolygonColor/_defaultPolygonThickness), adds it to Polygons (so it renders live
    /// while being drawn), deselects everything else, and makes it the EditablePolygon.</summary>
    /// <summary>True only between BeginEditingExistingOpenPolygon and that session ending (Finish/
    /// Close/Cancel) — distinguishes "re-opening an already-saved polyline to extend it" from
    /// "drawing a brand new one for the first time". Used by AddPolygonVertex to decide whether a
    /// click far from any existing segment should be allowed to prepend (extend from the START)
    /// rather than always appending to the end — sensible when picking up an existing line you
    /// might want to extend in either direction, but not necessarily wanted while freshly drawing a
    /// shape for the first time, where "always extend from wherever I just clicked" is the more
    /// natural default.</summary>
    private bool _isEditingExistingPolygon;

    /// <summary>Starts a free-count drawing: a polygon, or (kind = Curve) a smooth curve through the clicked
    /// points. Both take points until Finish/Close/Enter, so they share everything — the only difference is
    /// how the points are turned into an outline (see RefreshPolygonDisplay).</summary>
    private void BeginNewPolygon(ShapeKind kind = ShapeKind.Polygon)
    {
        IsSelectMode = false;
        IsMoveMode = false;
        AbandonCurrentDrawingIfAny();
        RememberStatusBeforeTool();
        foreach (var p in Polygons) p.IsSelected = false;

        var polygon = new PolygonViewModel
        {
            Kind = kind,
            StrokeColor = _defaultPolygonColor,
            StrokeThickness = _defaultPolygonThickness,
            HasStroke = _defaultHasStroke,
            FillColor = _defaultFillColor,
            FillOpacity = _defaultFillOpacity,
        };
        Polygons.Add(polygon);
        _drawingPolygon = polygon;
        _isEditingExistingPolygon = false;
        _polygonUndoStack.Clear();
        UpdateEditablePolygonFlags();
        StatusText = kind == ShapeKind.Curve
            ? "Curve: click points and a smooth curve is drawn through them  ·  drag a point to move it  ·  double-click a point to delete it  ·  " +
              "Enter closes it into a loop  ·  Esc or Finish leaves it open"
            : "Polygon: click to add points  ·  drag a point to move it  ·  double-click a point to delete it  ·  " +
              "Enter closes  ·  Esc leaves it open  ·  right-click for more";
    }

    /// <summary>
    /// Re-enters "drawing" mode for an EXISTING, already-saved OPEN polyline — lets the user add
    /// more points (extending it) or close/finish it, neither of which is otherwise possible once
    /// a polyline has only been selected rather than freshly drawn: ClosePolygonCommand/
    /// FinishPolygonCommand/Enter only act while _drawingPolygon is set, and ordinary selection
    /// doesn't set it. Triggered via the right-click "Edit Polyline" menu item. Does nothing for an
    /// already-closed polygon, since "add more points to extend it" isn't a meaningful operation
    /// on a closed loop (it's already fully editable via normal select + drag/insert/delete).
    /// </summary>
    public void BeginEditingExistingOpenPolygon(PolygonViewModel polygon)
    {
        // Only the free-count kinds can take more points. (An open ARC is also "not closed", but it is
        // defined by exactly three points — extending it would be meaningless.)
        if (polygon.IsClosed || polygon.HasFixedPointCount) return;
        RememberStatusBeforeTool();

        AbandonCurrentDrawingIfAny();
        foreach (var p in Polygons) p.IsSelected = false;

        _drawingPolygon = polygon;
        _isEditingExistingPolygon = true;
        _polygonUndoStack.Clear();
        UpdateEditablePolygonFlags();
        StatusText = "Editing polyline: click to add points (extends the nearer end)  ·  drag or double-click existing points  ·  " +
                     "Enter closes the loop  ·  Esc or Finish keeps it open";
    }

    /// <summary>
    /// Starts drawing a click-defined shape. <paramref name="kind"/> is ShapeKind.Polygon for a RECTANGLE (see
    /// _drawingKind), or Ellipse / CircleCenter / Circle3 / Arc3 / Text. A finished shape is an ordinary
    /// annotation, so everything downstream (right-click menu, editing, persistence, printing) treats it like
    /// any other.
    /// </summary>
    private void BeginNewShape(ShapeKind kind)
    {
        IsSelectMode = false;
        IsMoveMode = false;
        AbandonCurrentDrawingIfAny();
        RememberStatusBeforeTool();
        foreach (var p in Polygons) p.IsSelected = false;

        var polygon = new PolygonViewModel
        {
            // A rectangle stays a plain Polygon; every other shape is its own kind.
            Kind = kind,
            StrokeColor = _defaultPolygonColor,
            StrokeThickness = _defaultPolygonThickness,
            HasStroke = _defaultHasStroke,
            FillColor = _defaultFillColor,
            FillOpacity = _defaultFillOpacity,
            // Closed from the very start, not just once finished — BuildRenderPoints only draws the final
            // edge (back to the first vertex) when IsClosed is true, and leaving it false until the last
            // click meant the live preview was missing that edge. An arc (and text) is the exception: it
            // is open by nature.
            IsClosed = kind is not (ShapeKind.Arc3 or ShapeKind.Text),
        };
        if (kind == ShapeKind.Text)
            ApplyDefaultTextStyle(polygon);
        EnsureVisible(polygon); // "no stroke" remembered from an earlier filled shape must not give the next unfilled one nothing to show

        Polygons.Add(polygon);
        _drawingPolygon = polygon;
        _isDrawingShape = true;
        _drawingKind = kind;
        _shapePoints.Clear();
        _lastShapeCursor = null;
        _polygonUndoStack.Clear();
        UpdateEditablePolygonFlags();
        StatusText = kind switch
        {
            ShapeKind.Ellipse => "Ellipse: click one corner, then the opposite corner  ·  hold Ctrl for a circle  ·  hold Shift to draw from the centre  ·  Esc cancels",
            ShapeKind.CircleCenter => "Circle: click the centre, then a point on the edge  ·  Esc cancels",
            ShapeKind.Circle3 => "Circle: click three points that the circle passes through  ·  Esc cancels",
            ShapeKind.Arc3 => "Arc: click the start, a point on the arc, then the end  ·  Esc cancels",
            ShapeKind.Text => "Text: click where the text should go, then type  ·  Enter = new line  ·  Ctrl+Enter or click away to finish  ·  Esc cancels",
            _ => "Rectangle: click one corner, then the opposite corner  ·  Esc cancels",
        };
    }

    /// <summary>Called on each click while IsDrawingShape. Every click is recorded; the shape finishes itself
    /// on the last one it needs (ClicksNeeded) — no separate Finish/Close step. <paramref name="modifiers"/>
    /// are the keys held at THIS click (Ctrl/Shift matter only for an ellipse). The text tool is the one
    /// exception: MainWindow takes the click, calls PlaceEmptyText and opens the in-place editor instead.</summary>
    public void HandleShapeClick(double x, double y, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_drawingPolygon is null || !_isDrawingShape || _drawingKind == ShapeKind.Text) return;

        var point = new Point(x, y);
        _shapePoints.Add(point);
        _lastShapeCursor = point;
        ApplyShape(point, modifiers);

        if (_shapePoints.Count < ClicksNeeded(_drawingKind)) return; // more clicks to come: this was just a rubber-band update

        var finished = _drawingPolygon;
        _drawingPolygon = null;
        ResetShapeDrawingState();
        finished.IsSelected = true; // stays selected right after drawing, so color/fill/etc. can be adjusted immediately without re-clicking it
        RefreshPolygonDisplay(finished);
        UpdateEditablePolygonFlags();
        _ = SavePolygonsForCurrentPageAsync();
        RestoreStatusAfterTool(); // the tool is done: its instructions shouldn't linger in the title bar
    }

    /// <summary>Live rubber-band preview while moving the mouse between clicks — recomputes the whole shape on
    /// every move so it visibly tracks the cursor (and reflects Ctrl/Shift as they're held). Not an
    /// undo-tracked action; only the final placement is.</summary>
    public void UpdateShapePreview(double x, double y, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_drawingPolygon is null || !_isDrawingShape || _shapePoints.Count == 0 || _drawingKind == ShapeKind.Text) return;
        _lastShapeCursor = new Point(x, y);
        ApplyShape(_lastShapeCursor.Value, modifiers);
    }

    /// <summary>Redraws the preview at the last known cursor position — called when Ctrl or Shift is
    /// pressed/released mid-draw, so the circle/centre-point behaviour shows up immediately instead of
    /// only after the mouse next moves.</summary>
    public void RefreshShapePreview(ModifierKeys modifiers)
    {
        if (_drawingPolygon is null || !_isDrawingShape || _shapePoints.Count == 0 || _lastShapeCursor is null) return;
        ApplyShape(_lastShapeCursor.Value, modifiers);
    }

    private void ResetShapeDrawingState()
    {
        _isDrawingShape = false;
        _drawingKind = ShapeKind.Polygon;
        _shapePoints.Clear();
        _lastShapeCursor = null;
    }

    /// <summary>
    /// Rebuilds the shape-in-progress's control points from the clicks so far PLUS the live cursor standing in
    /// for every click still to come. So a 3-point circle with one click made is previewed as a line to the
    /// cursor (the control points [p1, cursor, cursor] don't define a circle yet — ShapeGeometry falls back to
    /// joining them), and with two clicks made becomes a real circle through p1, p2 and the cursor.
    /// </summary>
    private void ApplyShape(Point cursor, ModifierKeys modifiers)
    {
        if (_drawingPolygon is null) return;

        var pts = new List<Point>(_shapePoints);
        while (pts.Count < ClicksNeeded(_drawingKind)) pts.Add(cursor);

        List<PolygonVertexViewModel> vertices;
        switch (_drawingKind)
        {
            case ShapeKind.Ellipse:
                vertices = BuildEllipseControlPoints(pts[0], pts[1], modifiers);
                break;
            case ShapeKind.CircleCenter:
                vertices = new List<PolygonVertexViewModel>
                {
                    new() { X = pts[0].X, Y = pts[0].Y },
                    new() { X = pts[1].X, Y = pts[1].Y },
                };
                break;
            case ShapeKind.Circle3:
            case ShapeKind.Arc3:
                vertices = new List<PolygonVertexViewModel>
                {
                    new() { X = pts[0].X, Y = pts[0].Y },
                    new() { X = pts[1].X, Y = pts[1].Y },
                    new() { X = pts[2].X, Y = pts[2].Y },
                };
                break;
            default: // rectangle: the two clicks are opposite corners
                double minX = Math.Min(pts[0].X, pts[1].X), maxX = Math.Max(pts[0].X, pts[1].X);
                double minY = Math.Min(pts[0].Y, pts[1].Y), maxY = Math.Max(pts[0].Y, pts[1].Y);
                vertices = new List<PolygonVertexViewModel>
                {
                    new() { X = minX, Y = minY },
                    new() { X = maxX, Y = minY },
                    new() { X = maxX, Y = maxY },
                    new() { X = minX, Y = maxY },
                };
                break;
        }

        _drawingPolygon.Vertices = vertices;
        RefreshPolygonDisplay(_drawingPolygon);
        OnPropertyChanged(nameof(CanCloseCurrentPolygon));
    }

    /// <summary>
    /// The three control points of an ellipse: [centre, right-hand extent, bottom extent] — the ellipse itself
    /// is computed from those (see ShapeGeometry), so it's a true curve, and editing it later means dragging
    /// two radius handles and a centre, not hundreds of points. Like drawing software: by default the two
    /// clicks are opposite corners of the ellipse's bounding box; holding Ctrl forces a circle (the bounding
    /// box becomes a square, sized by the larger of the two drag distances, extending in the direction
    /// dragged); holding Shift treats the first click as the CENTRE and the second as a point on the
    /// ellipse's extent (so the radii are the full drag distances); Ctrl+Shift is a circle about the centre.
    /// </summary>
    private static List<PolygonVertexViewModel> BuildEllipseControlPoints(Point a, Point b, ModifierKeys modifiers)
    {
        bool circle = modifiers.HasFlag(ModifierKeys.Control);
        bool fromCentre = modifiers.HasFlag(ModifierKeys.Shift);

        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (circle)
        {
            double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -size : size;
            dy = dy < 0 ? -size : size;
        }

        double cx, cy, rx, ry;
        if (fromCentre)
        {
            cx = a.X; cy = a.Y;
            rx = Math.Abs(dx); ry = Math.Abs(dy);
        }
        else
        {
            cx = a.X + dx / 2; cy = a.Y + dy / 2;
            rx = Math.Abs(dx) / 2; ry = Math.Abs(dy) / 2;
        }

        return new List<PolygonVertexViewModel>
        {
            new() { X = cx, Y = cy },
            new() { X = cx + rx, Y = cy },
            new() { X = cx, Y = cy + ry },
        };
    }

    /// <summary>
    /// The text tool's click: turns the placeholder BeginNewShape(Text) created into a real (still empty) text
    /// annotation at the clicked point, selected, and hands it back so MainWindow can open the in-place editor on
    /// it. Nothing is saved yet — the annotation only becomes real once text is committed (CommitInlineText);
    /// cancelling or committing nothing removes it again.
    /// </summary>
    public PolygonViewModel? PlaceEmptyText(double x, double y)
    {
        if (_drawingPolygon is null || _drawingKind != ShapeKind.Text) return null;

        var polygon = _drawingPolygon;
        polygon.Text = "";
        ApplyDefaultTextStyle(polygon);
        polygon.Vertices = new List<PolygonVertexViewModel> { new() { X = x, Y = y } };

        _drawingPolygon = null;
        ResetShapeDrawingState();
        polygon.IsSelected = true;
        RefreshPolygonDisplay(polygon);
        UpdateEditablePolygonFlags();
        RestoreStatusAfterTool();
        return polygon;
    }

    /// <summary>
    /// The in-place editor finished with text. Real text is applied and saved; empty text on a brand-new annotation
    /// discards it (the person clicked, then changed their mind), and empty text on an existing one puts back what it
    /// had (clearing a label by accident shouldn't destroy it — Delete does that). <paramref name="originalText"/> is
    /// the text from before editing began: while typing, the annotation's own text follows the editor live (so its box
    /// and blurred backdrop grow with it), so it has to be restored rather than assumed unchanged.
    /// </summary>
    public void CommitInlineText(PolygonViewModel polygon, string text, bool isNew, string originalText = "")
    {
        polygon.IsInlineEditing = false;
        text = (text ?? "").Trim();

        if (text.Length == 0)
        {
            if (isNew)
            {
                RemoveUnsavedText(polygon);
            }
            else
            {
                polygon.Text = originalText;
                RefreshPolygonDisplay(polygon);
            }
            return;
        }

        polygon.Text = text;
        RefreshPolygonDisplay(polygon);
        UpdateEditablePolygonFlags();
        _ = SavePolygonsForCurrentPageAsync();
    }

    /// <summary>The in-place editor was cancelled (Escape): the text goes back to what it was, and a brand-new
    /// annotation goes away.</summary>
    public void CancelInlineText(PolygonViewModel polygon, bool isNew, string originalText = "")
    {
        polygon.IsInlineEditing = false;
        if (isNew)
        {
            RemoveUnsavedText(polygon);
            return;
        }
        polygon.Text = originalText;
        RefreshPolygonDisplay(polygon);
    }

    /// <summary>Called as the in-place editor's text changes, so the annotation's box (and the blurred backdrop behind
    /// it) follow what is being typed. Not saved: only a commit saves.</summary>
    public void PreviewInlineText(PolygonViewModel polygon, string text)
    {
        polygon.Text = text ?? "";
        RefreshPolygonDisplay(polygon);
    }

    /// <summary>Re-lays out and redraws one annotation (e.g. after the in-place editor opens, which strengthens its backdrop blur).</summary>
    public void RefreshTextDisplay(PolygonViewModel polygon) => RefreshPolygonDisplay(polygon);

    private void RemoveUnsavedText(PolygonViewModel polygon)
    {
        Polygons.Remove(polygon);
        UpdateEditablePolygonFlags();
    }

    // ---- Text formatting (toolbar) ----
    // Every control in the text toolbar edits the SAME thing: the style of the selected text annotation — or, when none is
    // selected, the style the NEXT text annotation will get (_textDefaults, which is just a PolygonViewModel used as a
    // style holder). So the controls always show something meaningful, and a change made with nothing selected carries
    // over to the next text placed. Sizes are in points, like a word processor.
    //
    // Until a size is picked, the default follows the page (about 1/70 of its height — 12pt on A4 — so it's readable on
    // any page size) rather than a fixed number that would be tiny on a 300 DPI scan and huge on a thumbnail.

    // Black letters, no outline, fully opaque — until the toolbar says otherwise (its Color / Thickness / Fill / Opacity controls
    // edit THESE while they are acting on text, and the shape defaults while they act on shapes; see StyleTargetsAreText).
    private readonly PolygonViewModel _textDefaults = new()
    {
        Kind = ShapeKind.Text,
        FillColor = Colors.Black,
        StrokeColor = Colors.Black,
        HasStroke = false,
        StrokeThickness = 1,
        FillOpacity = 100,
    };
    private bool _fontSizeChosen;

    private double DefaultFontSizePoints =>
        _fontSizeChosen ? _textDefaults.FontSizePoints : Math.Max(8, Math.Round(TextGeometry.DiuToPoints(_originalPageHeight / 70.0)));

    private static readonly string[] TextToolbarProperties =
    {
        nameof(CurrentTextFontSize), nameof(CurrentTextFontFamily), nameof(CurrentTextAlignment), nameof(CurrentTextBold), nameof(CurrentTextItalic),
        nameof(CurrentTextUnderline), nameof(CurrentTextRotation), nameof(CurrentTextCharacterSpacing), nameof(CurrentTextLineSpacing),
        nameof(CurrentTextBoxWidth), nameof(CurrentTextBlurBackground), nameof(CurrentTextBlurArea), nameof(CurrentTextBlurSoftness),
        nameof(CurrentFrameVisible), nameof(CurrentFrameStrokeOption), nameof(CurrentFrameThickness), nameof(CurrentFrameLineStyleOption),
        nameof(CurrentFrameFillOption), nameof(CurrentFrameOpacity), nameof(CurrentFramePadding), nameof(CurrentFrameRadius),
    };

    private PolygonViewModel? EditableText => EditablePolygon is { IsText: true } text ? text : null;

    /// <summary>Gives a new text annotation the toolbar's current style.</summary>
    /// <summary>Gives an annotation that would be invisible (no outline AND no fill — see <see cref="StyleRules"/>) something to be seen by: text gets its letters back in black,
    /// anything else gets its outline back. Left alone: pictures, and the Freeze/Hide bands (they are drawn their own way).</summary>
    private static void EnsureVisible(PolygonViewModel p)
    {
        if (p.IsImage || p.Purpose != PolygonPurpose.None) return;
        if (StyleRules.IsVisible(p.HasStroke, p.HasFill)) return;
        if (p.IsText) p.FillColor = Colors.Black; else p.HasStroke = true;
    }

    private void ApplyDefaultTextStyle(PolygonViewModel text)
    {
        text.FontFamilyName = _textDefaults.FontFamilyName;
        text.FontSizePoints = DefaultFontSizePoints;
        text.Alignment = _textDefaults.Alignment;
        text.Bold = _textDefaults.Bold;
        text.Italic = _textDefaults.Italic;
        text.Underline = _textDefaults.Underline;
        text.RotationDegrees = _textDefaults.RotationDegrees;
        text.CharacterSpacingPoints = _textDefaults.CharacterSpacingPoints;
        text.LineSpacing = _textDefaults.LineSpacing;
        text.BoxWidthPoints = _textDefaults.BoxWidthPoints;
        text.BlurBackground = _textDefaults.BlurBackground;
        text.BlurAreaPoints = _textDefaults.BlurAreaPoints;
        text.BlurSoftnessPercent = _textDefaults.BlurSoftnessPercent;
        text.FrameVisible = _textDefaults.FrameVisible;
        text.FrameStrokeColor = _textDefaults.FrameStrokeColor;
        text.FrameThickness = _textDefaults.FrameThickness;
        text.FrameLineStyle = _textDefaults.FrameLineStyle;
        text.FrameFillColor = _textDefaults.FrameFillColor;
        text.FrameOpacity = _textDefaults.FrameOpacity;
        text.FramePaddingPoints = _textDefaults.FramePaddingPoints;
        text.FrameCornerRadiusPoints = _textDefaults.FrameCornerRadiusPoints;
        text.FillColor = _textDefaults.FillColor;
        text.StrokeColor = _textDefaults.StrokeColor;
        text.HasStroke = _textDefaults.HasStroke;
        text.StrokeThickness = _textDefaults.StrokeThickness;
        text.FillOpacity = _textDefaults.FillOpacity;
        EnsureVisible(text);
    }

    /// <summary>Applies one formatting change to the defaults and — if a text annotation is selected or being placed — to it
    /// too, re-laying it out and saving it (a not-yet-finished one is saved when it finishes, like any new shape).</summary>
    private void ChangeTextStyle(Action<PolygonViewModel> change, string propertyName)
    {
        change(_textDefaults);
        if (EditableText is { } text)
        {
            change(text);
            RefreshPolygonDisplay(text);
            if (!ReferenceEquals(text, _drawingPolygon)) _ = SavePolygonsForCurrentPageAsync();
        }
        OnPropertyChanged(propertyName);
    }

    /// <summary>Font size in points.</summary>
    public double CurrentTextFontSize
    {
        get => EditableText?.FontSizePoints ?? DefaultFontSizePoints;
        set
        {
            _fontSizeChosen = true;
            ChangeTextStyle(t => t.FontSizePoints = value, nameof(CurrentTextFontSize));
        }
    }

    /// <summary>Every installed font family, alphabetically, for the toolbar's font box. Built once, on first use
    /// (enumerating the system fonts isn't free).</summary>
    private static IReadOnlyList<string>? _textFontFamilies;
    public static IReadOnlyList<string> TextFontFamilies =>
        _textFontFamilies ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    public string CurrentTextFontFamily
    {
        get => (EditableText ?? _textDefaults).FontFamilyName;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return; // the box reports null when the current font isn't installed here
            ChangeTextStyle(t => t.FontFamilyName = value, nameof(CurrentTextFontFamily));
        }
    }

    /// <summary>Left / centre / right / justified. Bound to a group of radio buttons through EnumToBooleanConverter.</summary>
    public TextAlign CurrentTextAlignment
    {
        get => (EditableText ?? _textDefaults).Alignment;
        set => ChangeTextStyle(t => t.Alignment = value, nameof(CurrentTextAlignment));
    }

    public bool CurrentTextBold
    {
        get => (EditableText ?? _textDefaults).Bold;
        set => ChangeTextStyle(t => t.Bold = value, nameof(CurrentTextBold));
    }

    public bool CurrentTextItalic
    {
        get => (EditableText ?? _textDefaults).Italic;
        set => ChangeTextStyle(t => t.Italic = value, nameof(CurrentTextItalic));
    }

    public bool CurrentTextUnderline
    {
        get => (EditableText ?? _textDefaults).Underline;
        set => ChangeTextStyle(t => t.Underline = value, nameof(CurrentTextUnderline));
    }

    /// <summary>Clockwise degrees about the text's top-left.</summary>
    public double CurrentTextRotation
    {
        get => (EditableText ?? _textDefaults).RotationDegrees;
        set => ChangeTextStyle(t => t.RotationDegrees = value, nameof(CurrentTextRotation));
    }

    /// <summary>Extra space after every character, in points.</summary>
    public double CurrentTextCharacterSpacing
    {
        get => (EditableText ?? _textDefaults).CharacterSpacingPoints;
        set => ChangeTextStyle(t => t.CharacterSpacingPoints = value, nameof(CurrentTextCharacterSpacing));
    }

    /// <summary>Line height as a multiple of the font's natural line height.</summary>
    public double CurrentTextLineSpacing
    {
        get => (EditableText ?? _textDefaults).LineSpacing;
        set => ChangeTextStyle(t => t.LineSpacing = value, nameof(CurrentTextLineSpacing));
    }

    /// <summary>Width of the text box in points (text wraps to it); 0 = automatic. Also set by dragging the box's right edge.</summary>
    public double CurrentTextBoxWidth
    {
        get => (EditableText ?? _textDefaults).BoxWidthPoints;
        set => ChangeTextStyle(t => t.BoxWidthPoints = value, nameof(CurrentTextBoxWidth));
    }

    public bool CurrentTextBlurBackground
    {
        get => (EditableText ?? _textDefaults).BlurBackground;
        set => ChangeTextStyle(t => t.BlurBackground = value, nameof(CurrentTextBlurBackground));
    }

    /// <summary>How far, in points, the blurred backdrop extends beyond the text box.</summary>
    public double CurrentTextBlurArea
    {
        get => (EditableText ?? _textDefaults).BlurAreaPoints;
        set => ChangeTextStyle(t => t.BlurAreaPoints = value, nameof(CurrentTextBlurArea));
    }

    /// <summary>How gradually the blur fades out towards the edge of its area, 0-100 (0 = a hard edge, 100 = fades across the whole area).</summary>
    public double CurrentTextBlurSoftness
    {
        get => (EditableText ?? _textDefaults).BlurSoftnessPercent;
        set => ChangeTextStyle(t => t.BlurSoftnessPercent = value, nameof(CurrentTextBlurSoftness));
    }

    /// <summary>The Text size box is only relevant while a text annotation is being placed or edited.</summary>
    public bool ShowTextOptions => EditablePolygon is { IsText: true };

    // ---- The selected picture: its options bar ----

    private PolygonViewModel? EditableImage => EditablePolygon is { IsImage: true } image ? image : null;

    /// <summary>The picture options bar (rotation, aspect-ratio lock) shows while a picture is the one being edited.</summary>
    public bool ShowImageOptions => EditablePolygon is { IsImage: true };

    /// <summary>Color, Thickness and Fill mean nothing to a picture, so the toolbar hides them while one is the selection. (Opacity and Include in Print stay.)</summary>
    public bool ShowShapeStyleControls => EditablePolygon is not { IsImage: true };

    private bool _keepImageAspect = true;
    /// <summary>The "Keep aspect ratio" checkbox: while on, dragging a picture's handles scales it in proportion. On by default — a stretched picture is
    /// rarely what anyone wants — and not saved with the picture: it is a setting of the dragging, not of the picture.</summary>
    public bool KeepImageAspect { get => _keepImageAspect; set => SetField(ref _keepImageAspect, value); }

    /// <summary>The selected picture's rotation in degrees, clockwise. Changing it turns the picture about its CENTRE (the box is anchored at its top-left,
    /// so the anchor moves to keep the centre where it was — otherwise the picture would swing round a corner).</summary>
    public double CurrentImageRotation
    {
        get => EditableImage?.RotationDegrees ?? 0;
        set
        {
            if (EditableImage is not { } image || image.Vertices.Count == 0) return;
            double rotation = Math.Clamp(value, -360, 360);
            var half = new GeoPoint(image.ImageWidth / 2, image.ImageHeight / 2);
            var centre = TextGeometry.LocalToPage(new GeoPoint(image.Vertices[0].X, image.Vertices[0].Y), image.RotationDegrees, half);
            var centreFromAnchor = TextGeometry.LocalToPage(new GeoPoint(0, 0), rotation, half);   // the centre's offset from the anchor at the NEW angle
            image.RotationDegrees = rotation;
            image.Vertices = new List<PolygonVertexViewModel> { new() { X = centre.X - centreFromAnchor.X, Y = centre.Y - centreFromAnchor.Y } };
            RefreshPolygonDisplay(image);
            _ = SavePolygonsForCurrentPageAsync();
            OnPropertyChanged(nameof(CurrentImageRotation));
        }
    }

    // ---- Frame (a rectangle round the text): the toolbar's frame controls, which act on the selected text and set what the next text gets.

    public IReadOnlyList<ColorOption> FrameStrokeOptions { get; } = BuildStandardColorPalette().ToList();
    public IReadOnlyList<ColorOption> FrameFillOptions { get; } = new[] { NoFillOption }.Concat(BuildStandardColorPalette()).ToList();
    public IReadOnlyList<FrameLineStyleOption> FrameLineStyleOptions { get; } = Enum.GetValues<FrameLineStyle>().Select(s => new FrameLineStyleOption(s)).ToList();

    public bool CurrentFrameVisible
    {
        get => (EditableText ?? _textDefaults).FrameVisible;
        set => ChangeTextStyle(t => t.FrameVisible = value, nameof(CurrentFrameVisible));
    }

    public ColorOption CurrentFrameStrokeOption
    {
        get { var c = (EditableText ?? _textDefaults).FrameStrokeColor; return FrameStrokeOptions.FirstOrDefault(o => o.Color == c) ?? FrameStrokeOptions[0]; }
        set { if (value is not null) ChangeTextStyle(t => t.FrameStrokeColor = value.Color, nameof(CurrentFrameStrokeOption)); }
    }

    public double CurrentFrameThickness
    {
        get => (EditableText ?? _textDefaults).FrameThickness;
        set => ChangeTextStyle(t => t.FrameThickness = value, nameof(CurrentFrameThickness));
    }

    public FrameLineStyleOption CurrentFrameLineStyleOption
    {
        get { var s = (EditableText ?? _textDefaults).FrameLineStyle; return FrameLineStyleOptions.First(o => o.Style == s); }
        set { if (value is not null) ChangeTextStyle(t => t.FrameLineStyle = value.Style, nameof(CurrentFrameLineStyleOption)); }
    }

    public ColorOption CurrentFrameFillOption
    {
        get
        {
            var c = (EditableText ?? _textDefaults).FrameFillColor;
            return c is null ? FrameFillOptions[0] : FrameFillOptions.FirstOrDefault(o => !o.IsNoFill && o.Color == c) ?? FrameFillOptions[0];
        }
        set { if (value is not null) ChangeTextStyle(t => t.FrameFillColor = value.IsNoFill ? null : value.Color, nameof(CurrentFrameFillOption)); }
    }

    public double CurrentFrameOpacity
    {
        get => (EditableText ?? _textDefaults).FrameOpacity;
        set => ChangeTextStyle(t => t.FrameOpacity = value, nameof(CurrentFrameOpacity));
    }

    public double CurrentFramePadding
    {
        get => (EditableText ?? _textDefaults).FramePaddingPoints;
        set => ChangeTextStyle(t => t.FramePaddingPoints = value, nameof(CurrentFramePadding));
    }

    public double CurrentFrameRadius
    {
        get => (EditableText ?? _textDefaults).FrameCornerRadiusPoints;
        set => ChangeTextStyle(t => t.FrameCornerRadiusPoints = value, nameof(CurrentFrameRadius));
    }

    /// <summary>Gives the selected picture back the proportions of the file it came from, keeping its width (for after the aspect lock was off and it got stretched).</summary>
    public void ResetImageProportions()
    {
        if (EditableImage is not { ImageSource: { PixelWidth: > 0, PixelHeight: > 0 } source } image) return;
        image.ImageHeight = image.ImageWidth * source.PixelHeight / source.PixelWidth;
        RefreshPolygonDisplay(image);
        _ = SavePolygonsForCurrentPageAsync();
        StatusText = "Picture restored to its original proportions.";
    }

    /// <summary>"Include in Print" applies to everything except a Hide Row/Column...</summary>
    public bool ShowIncludeInPrint
    {
        get
        {
            // One item: as ever. Several selected at once: the checkbox is there too, acting on all of them (a hide band among them keeps its own meaning and is left out — see IncludeInPrintTargets).
            var selected = Polygons.Where(p => p.IsSelected).ToList();
            return selected.Count >= 2 ? selected.Any(p => !p.IsHidden) : EditablePolygon is { IsHidden: false };
        }
    }

    /// <summary>What the "Include in Print" box acts on: everything selected — except that when ordinary shapes and Hide Row/Column bands are selected together, only the shapes. (For a band the same
    /// flag means "apply this hide to the printout", a different decision, with its own "Hide in Print" box that shows when only bands are selected.)</summary>
    private List<PolygonViewModel> IncludeInPrintTargets()
    {
        var targets = GetStyleTargets().ToList();
        return targets.Any(p => !p.IsHidden) ? targets.Where(p => !p.IsHidden).ToList() : targets;
    }

    /// <summary>...for which the same flag is shown as "Hide in Print" instead (see PolygonAnnotation.IncludeInPrint).</summary>
    public bool ShowHideInPrint
    {
        get
        {
            var selected = Polygons.Where(p => p.IsSelected).ToList();
            return selected.Count >= 2 ? selected.All(p => p.IsHidden) : EditablePolygon is { IsHidden: true };
        }
    }

    /// <summary>If a polygon or rectangle was left mid-draw (e.g. the person clicked "Polygon" or
    /// "Rectangle" again without finishing the previous one), drops the abandoned, never-saved
    /// attempt rather than leaving it orphaned in Polygons.</summary>
    // The title bar's status message doubles as the active drawing tool's instructions ("Polygon: click to add
    // points ..."). Those are only true while the tool is active, so whatever the message was before the tool
    // started is remembered and put back when it ends, however it ends (finished, closed, cancelled, shape
    // completed). Starting a different tool while one is active keeps the ORIGINAL message, not the previous tool's hint.
    private string? _statusBeforeTool;

    private void RememberStatusBeforeTool() => _statusBeforeTool ??= _statusText;

    private void RestoreStatusAfterTool()
    {
        if (_statusBeforeTool is null) return;
        StatusText = _statusBeforeTool;
        _statusBeforeTool = null;
    }

    private void AbandonCurrentDrawingIfAny()
    {
        if (_drawingPolygon is not null)
        {
            Polygons.Remove(_drawingPolygon);
            _drawingPolygon = null;
        }
        ResetShapeDrawingState();
        _isEditingExistingPolygon = false;
    }

    private async Task FinishPolygonAsync()
    {
        if (_drawingPolygon is null) return;

        var finished = _drawingPolygon;
        if (finished.Vertices.Count < 2)
        {
            // Not enough points to be a meaningful polygon — discard rather than save a stray dot.
            Polygons.Remove(finished);
            finished = null;
        }
        _drawingPolygon = null;
        ResetShapeDrawingState();
        _isEditingExistingPolygon = false;
        if (finished is not null)
            finished.IsSelected = true; // stays selected right after finishing, so color/fill/etc. can be adjusted immediately
        UpdateEditablePolygonFlags();
        RestoreStatusAfterTool();
        await SavePolygonsForCurrentPageAsync();
    }

    /// <summary>Connects the last vertex back to the first (closing the polygon), then finishes and
    /// saves — triggered by the Enter key, the toolbar button, or the right-click context menu.</summary>
    private async Task ClosePolygonAsync()
    {
        if (_drawingPolygon is null || _drawingPolygon.Vertices.Count < 3) return;

        _drawingPolygon.IsClosed = true;
        RefreshPolygonDisplay(_drawingPolygon);
        var finished = _drawingPolygon;
        _drawingPolygon = null;
        ResetShapeDrawingState();
        _isEditingExistingPolygon = false;
        finished.IsSelected = true; // stays selected right after closing, so color/fill/etc. can be adjusted immediately
        UpdateEditablePolygonFlags();
        RestoreStatusAfterTool();
        await SavePolygonsForCurrentPageAsync();
    }

    /// <summary>Discards the in-progress polygon/rectangle (if it was never usefully started) or
    /// reverts an existing polygon's edits back to its last-saved state — triggered by Escape.
    /// Always reloads from the persisted state rather than special-casing "was this a brand-new
    /// polygon or an existing one being re-opened via BeginEditingExistingOpenPolygon" — reloading
    /// already correctly handles both: a never-saved polygon was never in the persisted JSON to
    /// begin with, so it's naturally dropped, while an existing one's in-progress edits are
    /// naturally reverted to what was last saved.</summary>
    private void CancelPolygonEdit()
    {
        _drawingPolygon = null;
        ResetShapeDrawingState();
        _isEditingExistingPolygon = false;
        RestoreStatusAfterTool();
        RefreshPolygonsForCurrentPageAndVariant();
    }

    private async Task DeleteSelectedPolygonsAsync()
    {
        var toRemove = Polygons.Where(p => p.IsSelected).ToList();
        if (toRemove.Count == 0) return;

        bool anyHidden = toRemove.Any(p => p.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn);

        foreach (var p in toRemove)
        {
            Polygons.Remove(p);
            if (ReferenceEquals(_drawingPolygon, p))
                _drawingPolygon = null;
        }
        UpdateEditablePolygonFlags();
        if (anyHidden)
            RefreshHiddenBandsAndRecomposite(); // deleting a Hide Row/Column un-hides that band
        await SavePolygonsForCurrentPageAsync();
    }

    public void SetPurposeForSelection(PolygonPurpose purpose)
    {
        var targets = GetStyleTargets().ToList();
        // Freeze/Hide read a shape's extent from its vertices, which is only the true extent for a
        // polygon/rectangle — a circle's control points are not its bounding box. (The context menu doesn't
        // offer these for other kinds; this is the backstop.)
        if (purpose != PolygonPurpose.None) targets = targets.Where(p => p.Kind == ShapeKind.Polygon).ToList();
        if (targets.Count == 0) return;

        if (purpose is PolygonPurpose.FreezeRow or PolygonPurpose.FreezeColumn or PolygonPurpose.HideRow or PolygonPurpose.HideColumn)
        {
            // These have real preconditions (closed, spans the full page width/height) — each
            // polygon in the selection is validated independently rather than blindly assigned.
            foreach (var p in targets)
                TryAssignSpanningPurpose(p, purpose);
            return;
        }

        // Covers clearing a purpose back to None (Unfreeze/Unhide) — if any target WAS a Hide
        // Row/Column, clearing it un-hides that band, so the bitmap needs recompositing.
        bool anyWasHidden = targets.Any(p => p.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn);
        foreach (var p in targets) p.Purpose = purpose;
        if (anyWasHidden)
            RefreshHiddenBandsAndRecomposite();
        _ = SavePolygonsForCurrentPageAsync();
    }

    /// <summary>
    /// Validates and assigns a Freeze Row/Column or Hide Row/Column purpose to a polygon. Only
    /// checks the polygon's own shape validity (closed, enough points) — does NOT require or
    /// enforce spanning the full page width/height, and does not attempt to extend the boundary.
    /// A polygon narrower/shorter than the page is simply frozen/hidden using its own actual
    /// extent.
    /// </summary>
    private void TryAssignSpanningPurpose(PolygonViewModel polygon, PolygonPurpose purpose)
    {
        string purposeName = purpose switch
        {
            PolygonPurpose.FreezeRow => "Freeze Row",
            PolygonPurpose.FreezeColumn => "Freeze Column",
            PolygonPurpose.HideRow => "Hide Row",
            PolygonPurpose.HideColumn => "Hide Column",
            _ => purpose.ToString(),
        };

        if (!polygon.IsClosed)
        {
            MessageBox.Show(
                $"{purposeName} requires a closed polygon. Close it first (Enter, the toolbar button, or right-click > Close Polygon), then try again.",
                purposeName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (polygon.Vertices.Count < 3)
        {
            MessageBox.Show(
                $"{purposeName} failed: the polygon has too few points to form a valid shape.",
                purposeName, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        polygon.Purpose = purpose;
        RefreshHiddenBandsAndRecomposite();
        _ = SavePolygonsForCurrentPageAsync();
        StatusText = $"{purposeName} applied.";
    }

    /// <summary>Selects a polygon (used by both left-click selection and right-click-to-select-
    /// before-showing-a-context-menu). addToSelection=true (Ctrl held) toggles it into/out of the
    /// existing selection; false clears everything else first (normal single-select click).</summary>
    public void SelectPolygon(PolygonViewModel polygon, bool addToSelection)
    {
        if (addToSelection)
        {
            polygon.IsSelected = !polygon.IsSelected;
        }
        else
        {
            foreach (var p in Polygons) p.IsSelected = ReferenceEquals(p, polygon);
        }
        UpdateEditablePolygonFlags();
        RaiseStyleControlsChanged();
        RaiseStyleControlsChangedAfterBinding(); // and once more when the boxes have finished reacting to the new list, so a selection they dropped is put back
    }

    /// <summary>If the polygon isn't already part of the current selection, makes it the sole
    /// selection — mirrors how right-click normally behaves in CAD/vector tools (right-clicking an
    /// unselected item selects it before showing its menu; right-clicking within an existing
    /// multi-selection preserves it).</summary>
    public void EnsureSelectedForContextMenu(PolygonViewModel polygon)
    {
        if (!polygon.IsSelected)
            SelectPolygon(polygon, addToSelection: false);
    }

    public void ClearPolygonSelection()
    {
        if (Polygons.All(p => !p.IsSelected)) return;
        foreach (var p in Polygons) p.IsSelected = false;
        UpdateEditablePolygonFlags();
        RaiseStyleControlsChanged();
        RaiseStyleControlsChangedAfterBinding();
    }

    private void UpdateEditablePolygonFlags()
    {
        var editable = EditablePolygon;
        foreach (var p in Polygons) p.IsEditable = ReferenceEquals(p, editable);
        OnPropertyChanged(nameof(EditablePolygon));
        OnPropertyChanged(nameof(IsEditingPolygon));
        OnPropertyChanged(nameof(IsDrawingPolygon));
        OnPropertyChanged(nameof(IsDrawingShape));
        OnPropertyChanged(nameof(IsDrawingEllipse));
        OnPropertyChanged(nameof(IsPlacingText));
        OnPropertyChanged(nameof(ShowTextOptions));
        OnPropertyChanged(nameof(ShowImageOptions));
        OnPropertyChanged(nameof(ShowShapeStyleControls));
        OnPropertyChanged(nameof(CurrentImageRotation));
        RaiseUndoRedoChanged();
        foreach (var name in TextToolbarProperties) OnPropertyChanged(name);
        OnPropertyChanged(nameof(ShowIncludeInPrint));
        OnPropertyChanged(nameof(ShowHideInPrint));
        OnPropertyChanged(nameof(IsDrawingFreeformPolygon));
        OnPropertyChanged(nameof(CanCloseCurrentPolygon));
        // The style combo boxes (color/thickness/fill/opacity) reflect whichever polygon is now
        // editable — e.g. a just-finished rectangle staying selected, or focus moving to a
        // different polygon — so they need to refresh here too, not just on explicit selection
        // changes (SelectPolygon/ClearPolygonSelection already do their own).
        RaiseStyleControlsChanged();
        _polygonUndoStack.Clear();
    }

    /// <summary>
    /// Adds a new vertex to EditablePolygon at the given coordinates (WPF device-independent
    /// units — see MainWindow.xaml.cs's GetImageSpacePosition for how these are computed from a
    /// click). If the click landed near an EXISTING line segment (including the closing segment on
    /// an already-closed polygon), the vertex is inserted between that segment's two endpoints —
    /// splitting it — rather than always appended to the end, which would land it on the wrong
    /// segment for a closed shape.
    ///
    /// Otherwise, when re-editing an existing open polyline (_isEditingExistingPolygon — see
    /// BeginEditingExistingOpenPolygon), the click extends whichever END of the polyline it's
    /// actually closer to: prepended if nearer the first vertex, appended if nearer the last.
    /// Previously this always appended, so trying to pick a re-opened polyline back up and extend
    /// it from where it originally STARTED instead silently connected to the wrong end. This is
    /// deliberately NOT applied while freshly drawing a polygon for the first time (_drawingPolygon
    /// but !_isEditingExistingPolygon) — there, "always extend from wherever was just clicked" is
    /// the expected, unsurprising default; nearest-endpoint logic could instead misfire if an
    /// in-progress shape happens to loop back near its own start.
    /// </summary>
    public void AddPolygonVertex(double x, double y, double tolerance)
    {
        var target = EditablePolygon;
        if (target is null || target.HasFixedPointCount) return; // a circle through 3 points can't take a 4th

        // Refuse to add a new point too close to an EXISTING one — a near-duplicate point right
        // next to another is confusing clutter, and easy to create by accident while trying to
        // drag. MainWindow now grabs the nearest vertex for any press within this same tolerance
        // (see FreezeEditCanvas_PreviewMouseLeftButtonDown), so in normal use a click that close
        // never reaches here at all; this stays as a backstop for any other caller.
        foreach (var vertex in target.Vertices)
        {
            double distToVertex = Math.Sqrt(Math.Pow(x - vertex.X, 2) + Math.Pow(y - vertex.Y, 2));
            if (distToVertex < tolerance)
                return;
        }

        var updated = new List<PolygonVertexViewModel>(target.Vertices);
        int segmentIndex = FindNearestSegmentIndex(target.Vertices, target.IsClosed, x, y, tolerance);

        if (segmentIndex >= 0)
        {
            updated.Insert(segmentIndex + 1, new PolygonVertexViewModel { X = x, Y = y });
        }
        else if (_isEditingExistingPolygon && !target.IsClosed && updated.Count >= 2)
        {
            var first = updated[0];
            var last = updated[^1];
            double distToFirst = Math.Sqrt(Math.Pow(x - first.X, 2) + Math.Pow(y - first.Y, 2));
            double distToLast = Math.Sqrt(Math.Pow(x - last.X, 2) + Math.Pow(y - last.Y, 2));

            if (distToFirst < distToLast)
                updated.Insert(0, new PolygonVertexViewModel { X = x, Y = y });
            else
                updated.Add(new PolygonVertexViewModel { X = x, Y = y });
        }
        else
        {
            updated.Add(new PolygonVertexViewModel { X = x, Y = y });
        }

        ApplyVertexList(target, updated);
    }

    private static int FindNearestSegmentIndex(List<PolygonVertexViewModel> vertices, bool isClosed, double x, double y, double tolerance)
    {
        if (vertices.Count < 2) return -1;

        int segmentCount = isClosed ? vertices.Count : vertices.Count - 1;
        int bestIndex = -1;
        double bestDistance = tolerance;
        for (int i = 0; i < segmentCount; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            double distance = DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>Call ONCE when a drag gesture starts (mouse-down on an existing vertex) — snapshots
    /// the pre-drag state for undo. MovePolygonVertex, called repeatedly during the drag, does NOT
    /// push its own snapshots, so a whole "A to B" drag becomes a single undo step.</summary>
    public void BeginVertexDrag(int index)
    {
        var target = EditablePolygon;
        if (target is null || index < 0 || index >= target.Vertices.Count) return;
        _polygonUndoStack.Push((new List<PolygonVertexViewModel>(target.Vertices), target.IsClosed));
    }

    /// <summary>Call once when the drag gesture ends (mouse-up). If the polygon being dragged is a
    /// Hide Row/Column, this is where the (expensive) hidden-band recompositing actually happens —
    /// MovePolygonVertex intentionally skips it on every tick during the drag itself, since copying
    /// the whole image's pixel buffer dozens of times per second would make dragging visibly laggy.</summary>
    public void EndVertexDrag()
    {
        if (EditablePolygon?.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn)
            RefreshHiddenBandsAndRecomposite();
    }

    public void MovePolygonVertex(int index, double x, double y)
    {
        var target = EditablePolygon;
        if (target is null || index < 0 || index >= target.Vertices.Count) return;
        var updated = new List<PolygonVertexViewModel>(target.Vertices);

        switch (target.Kind)
        {
            // Ellipse: [centre, right extent, bottom extent]. Dragging the centre moves the whole ellipse; each
            // extent handle can only slide along its own axis (it IS a radius), so the shape stays a clean
            // axis-aligned ellipse however the handles are pulled.
            case ShapeKind.Ellipse:
            {
                double cx = updated[0].X, cy = updated[0].Y;
                if (index == 0) TranslateAll(updated, x - cx, y - cy);
                else if (index == 1) updated[1] = new PolygonVertexViewModel { X = cx + Math.Max(1, Math.Abs(x - cx)), Y = cy };
                else updated[2] = new PolygonVertexViewModel { X = cx, Y = cy + Math.Max(1, Math.Abs(y - cy)) };
                break;
            }
            // Circle (centre, edge): the centre moves the whole circle; the edge handle is free (it sets the radius).
            case ShapeKind.CircleCenter when index == 0:
            case ShapeKind.Text:
            case ShapeKind.Image:
                TranslateAll(updated, x - updated[index].X, y - updated[index].Y);
                break;
            default:
                updated[index] = new PolygonVertexViewModel { X = x, Y = y };
                break;
        }
        ApplyVertexList(target, updated, pushUndo: false);
    }

    private static void TranslateAll(List<PolygonVertexViewModel> vertices, double dx, double dy)
    {
        for (int i = 0; i < vertices.Count; i++)
            vertices[i] = new PolygonVertexViewModel { X = vertices[i].X + dx, Y = vertices[i].Y + dy };
    }

    public void RemovePolygonVertex(int index)
    {
        var target = EditablePolygon;
        if (target is null || target.HasFixedPointCount || index < 0 || index >= target.Vertices.Count) return;
        var updated = new List<PolygonVertexViewModel>(target.Vertices);
        updated.RemoveAt(index);
        ApplyVertexList(target, updated);
    }

    public int FindNearestVertexIndex(double x, double y, double tolerance)
    {
        var target = EditablePolygon;
        if (target is null) return -1;
        int bestIndex = -1;
        double bestDistance = tolerance;
        for (int i = 0; i < target.Vertices.Count; i++)
        {
            double dx = target.Vertices[i].X - x;
            double dy = target.Vertices[i].Y - y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>Finds whichever polygon's line or vertex is nearest the given point, within
    /// tolerance — used both for left-click selection and right-click context-menu targeting.
    /// Searches ALL polygons (not just the editable one), so you can select/right-click a polygon
    /// that isn't currently being edited. For a FILLED closed polygon specifically, a click ANYWHERE
    /// inside its interior also counts — since that area is visually solid, clicking "on it" is the
    /// natural interaction, not just clicking precisely on its edge. Scoped to HasFill (not every
    /// closed polygon) so click-through behavior for Freeze/Hide/purposeless polygons — whose
    /// interiors aren't visually distinct — is unaffected.</summary>
    public PolygonViewModel? FindPolygonNearPoint(double x, double y, double tolerance)
    {
        // Topmost first: what is later in the list is drawn later, so is on top. (It used to go front-to-back, so a rectangle
        // drawn before some text always won, and the text could not be clicked.)
        var topDown = Polygons.Reverse().ToList();

        // Pass 1 — PRECISE hits, anywhere on the page: on text, on a Hide's break line, on an outline or a control point. These
        // beat the interior of any filled shape, however far up or down the stack that shape is — so text sitting on a filled
        // rectangle is chosen when you click the text, and the rectangle when you click its edge or the empty part of it.
        foreach (var polygon in topDown)
        {
            if (polygon.Vertices.Count == 0) continue;

            // A Hide Row/Column is only ever SEEN as its dashed break line at the collapsed seam — its own
            // outline collapses to nothing once hidden (every vertex maps onto the seam). So it is hit-tested
            // as that line, in DISPLAY space, and nothing else. (It used to be tested against its
            // original-space outline, which sits at coordinates that no longer correspond to anything on screen:
            // a click on the line you can see only registered if it happened to fall near the hidden
            // rectangle's edge — hence having to click above, below and around it until it took.)
            if (polygon.IsHidden)
            {
                if (IsPointNearBreakLine(polygon, x, y, tolerance)) return polygon;
                continue;
            }

            if (polygon.IsText)
            {
                if (IsPointInTextBounds(polygon, x, y, tolerance)) return polygon;
                continue;
            }

            if (polygon.IsImage) continue; // a picture is a solid area: it is picked in the second pass, like a filled shape

            if (IsPointNearPolyline(GetOutline(polygon), polygon.IsClosed, x, y, tolerance))
                return polygon;
            // Also check individual vertices directly, in case tolerance/segment math misses a
            // lone or very short polygon (e.g. a single point, or two points very close together).
            for (int i = 0; i < polygon.Vertices.Count; i++)
            {
                double dx = polygon.Vertices[i].X - x, dy = polygon.Vertices[i].Y - y;
                if (Math.Sqrt(dx * dx + dy * dy) <= tolerance)
                    return polygon;
            }
        }

        // Pass 2 — the INTERIOR of a filled shape, again topmost first.
        foreach (var polygon in topDown)
        {
            if (polygon.IsImage)
            {
                // In stacking order with the filled shapes: topmost wins. (Text and outlines on top of a picture were already found above.)
                if (IsPointInImageBox(polygon, x, y, tolerance)) return polygon;
                continue;
            }
            if (polygon.IsText || polygon.IsHidden || !polygon.HasFill) continue;
            var outline = GetOutline(polygon);
            if (outline.Count >= 3 && IsPointInsidePolygon(outline, x, y))
                return polygon;
        }
        return null;
    }

    /// <summary>
    /// Whether a click (in page units) is on a Hide Row/Column's visible break line. The click is converted
    /// to display space — through the same hidden-band mapping the line itself was positioned with — and
    /// compared against the line there, so it works wherever on screen the line is, however far the page is
    /// zoomed, and whichever side of the collapsed seam the click lands (a click just BELOW the seam maps back
    /// to a page position beyond the whole hidden band, which is exactly why comparing in page space missed it).
    /// <paramref name="tolerance"/> is in page units (the callers' 10/zoom), so ×Zoom is the same 10 screen pixels;
    /// a break line is thin, so it gets at least a comfortable 8.
    /// </summary>
    private bool IsPointNearBreakLine(PolygonViewModel polygon, double x, double y, double tolerance)
    {
        if (polygon.BreakLinePoints is not { Count: >= 2 } line) return false;

        double displayX = MapToDisplay(x, _hiddenXRanges) * Zoom;
        double displayY = MapToDisplay(y, _hiddenYRanges) * Zoom;
        double reach = Math.Max(tolerance * Zoom, 8.0);
        return DistanceToSegment(displayX, displayY, line[0].X, line[0].Y, line[1].X, line[1].Y) <= reach;
    }

    /// <summary>Standard ray-casting point-in-polygon test.</summary>
    private static bool IsPointInsidePolygon(List<PolygonVertexViewModel> vertices, double x, double y)
    {
        bool inside = false;
        int j = vertices.Count - 1;
        for (int i = 0; i < vertices.Count; i++)
        {
            double xi = vertices[i].X, yi = vertices[i].Y;
            double xj = vertices[j].X, yj = vertices[j].Y;

            bool crosses = (yi > y) != (yj > y) &&
                x < (xj - xi) * (y - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
            j = i;
        }
        return inside;
    }

    /// <summary>Checks whether a point is near a SPECIFIC polygon's line (not vertices) — used by
    /// code-behind to decide whether a click near the currently-editable polygon should continue
    /// editing it, vs. being treated as a click elsewhere that should change the selection.</summary>
    public bool IsPointNearPolygonLine(PolygonViewModel polygon, double x, double y, double tolerance)
    {
        if (polygon.IsText) return IsPointInTextBounds(polygon, x, y, tolerance);
        if (polygon.IsImage) return IsPointInImageBox(polygon, x, y, tolerance);
        var outline = GetOutline(polygon);
        return outline.Count >= 2 && IsPointNearPolyline(outline, polygon.IsClosed, x, y, tolerance);
    }

    private static bool IsPointNearPolyline(List<PolygonVertexViewModel> vertices, bool isClosed, double x, double y, double tolerance)
    {
        int segmentCount = isClosed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            if (DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y) <= tolerance)
                return true;
        }
        return false;
    }

    private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            double ddx = px - x1, ddy = py - y1;
            return Math.Sqrt(ddx * ddx + ddy * ddy); // degenerate (zero-length) segment
        }

        double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0, 1);
        double closestX = x1 + t * dx;
        double closestY = y1 + t * dy;
        double distX = px - closestX, distY = py - closestY;
        return Math.Sqrt(distX * distX + distY * distY);
    }

    /// <summary>Recomputes hidden ranges from the CURRENT Polygons, refreshes every polygon's
    /// display to match (positions may shift if a hide's extent changed), and recomposites
    /// PreviewImage. This does real work (copies the whole image's pixel buffer), so it's only
    /// called after DISCRETE actions (assigning/clearing a purpose, deleting a polygon, adding/
    /// removing a point) — never on every tick of a live vertex drag, which would make dragging
    /// visibly laggy on a large page. See EndVertexDrag for how a completed drag catches up once.</summary>
    private void RefreshHiddenBandsAndRecomposite()
    {
        RecomputeHiddenBands();
        RefreshAllPolygonDisplays();
        RefreshPreviewImage();
    }

    private void ApplyVertexList(PolygonViewModel target, List<PolygonVertexViewModel> updated, bool pushUndo = true)
    {
        if (pushUndo)
            _polygonUndoStack.Push((new List<PolygonVertexViewModel>(target.Vertices), target.IsClosed));

        target.Vertices = updated;

        // A "closed" shape with fewer than 3 points is geometrically meaningless — you need at
        // least 3 vertices to form a loop; 2 points can only ever be a single open line segment.
        // Without this, deleting a vertex down to 2 left the polygon internally marked IsClosed
        // while having no valid closing segment to speak of — which broke two things at once:
        // "Edit Polyline" stayed hidden (it's gated on !IsClosed), and segment-insertion picked up
        // a degenerate wraparound "closing" segment identical to the one real segment, so a tie in
        // FindNearestSegmentIndex's distance comparison could pick that phantom segment and insert
        // at the very end of the list instead of splitting the line where actually clicked.
        //
        // Free-count kinds only: a circle by centre and radius legitimately has just two control points yet
        // is a closed shape, and un-closing it here would turn it into an open ring (and lose its fill) the
        // moment its edge handle was dragged.
        if (!target.HasFixedPointCount && target.Vertices.Count < 3 && target.IsClosed)
            target.IsClosed = false;

        // pushUndo=true means this is a discrete action (add/remove a point) rather than a
        // per-tick live-drag move (see MovePolygonVertex, which always passes false) — cheap
        // enough to fully recompute immediately if this polygon affects what's hidden.
        if (pushUndo && target.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn)
            RefreshHiddenBandsAndRecomposite();
        else
            RefreshPolygonDisplay(target);

        OnPropertyChanged(nameof(CanCloseCurrentPolygon));

        // A brand-new polygon isn't saved until Finish/Close (matches the old behavior); an
        // EXISTING polygon being re-opened via selection has no separate "Finish" step in this
        // model, so it autosaves after every edit instead.
        if (!ReferenceEquals(target, _drawingPolygon))
            _ = SavePolygonsForCurrentPageAsync();
    }

    /// <summary>Restores EditablePolygon's vertex list (AND its IsClosed state — see
    /// _polygonUndoStack's docs for why both are snapshotted together) to how it was immediately
    /// before the last add/move/remove — triggered by Ctrl+Z.</summary>
    public void UndoPolygonEdit()
    {
        var target = EditablePolygon;
        if (target is null || _polygonUndoStack.Count == 0) return;

        var (vertices, isClosed) = _polygonUndoStack.Pop();
        target.Vertices = vertices;
        target.IsClosed = isClosed;

        if (target.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn)
            RefreshHiddenBandsAndRecomposite();
        else
            RefreshPolygonDisplay(target);

        OnPropertyChanged(nameof(CanCloseCurrentPolygon));
    }

    /// <summary>Rebuilds one polygon's zoom-scaled DisplayVertices/DisplayPoints from its raw
    /// Vertices and the current Zoom. Called whenever that polygon's vertices change, OR (for every
    /// polygon) whenever Zoom changes — see the Zoom property setter. Vertices are first mapped
    /// through MapToDisplay (for BOTH axes) so a polygon positioned below/right of a hidden row/
    /// column correctly shifts to match the now gap-closed image — this runs for every polygon,
    /// not just Hide ones. Also rebuilds FreezeClipGeometry for Freeze Row/Column polygons, using
    /// the polygon's own actual vertices as-drawn (no forced full-width/height extension).
    /// For Hide Row/Column, also computes BreakLinePoints — since a hidden polygon's own outline
    /// collapses to a degenerate shape once mapped (every vertex lands on the same seam), its
    /// DisplayPoints aren't rendered directly; this simple two-point seam line replaces it.</summary>
    private void RefreshPolygonDisplay(PolygonViewModel polygon)
    {
        var scaled = polygon.Vertices
            .Select(v => new PolygonVertexViewModel
            {
                X = MapToDisplay(v.X, _hiddenXRanges) * Zoom,
                Y = MapToDisplay(v.Y, _hiddenYRanges) * Zoom,
            })
            .ToList();
        polygon.DisplayVertices = scaled;
        if (polygon.IsText) UpdateTextDisplay(polygon);
        if (polygon.IsImage) UpdateImageDisplay(polygon);

        if (polygon.Kind is ShapeKind.Polygon or ShapeKind.Text or ShapeKind.Image)
        {
            polygon.DisplayPoints = BuildRenderPoints(scaled, polygon.IsClosed);
        }
        else
        {
            // A parametric shape's vertices are just its control points (the handles). The outline drawn is
            // the true curve, flattened HERE — in screen space, after scaling — so its smoothness is judged in
            // screen pixels and it stays smooth however far the page is zoomed in (flattening once in page
            // units and scaling the result would turn into visible facets at high zoom). Half a screen pixel
            // of chord deviation is invisible.
            var flat = ShapeGeometry.Flatten(polygon.Kind, ToGeo(scaled), polygon.IsClosed, 0.3);
            polygon.DisplayPoints = BuildRenderPoints(flat.Select(g => new PolygonVertexViewModel { X = g.X, Y = g.Y }).ToList(), polygon.IsClosed);
        }

        if (polygon.IsFrozen && polygon.IsClosed && polygon.Vertices.Count >= 3)
        {
            polygon.FreezeClipGeometry = BuildClipGeometry(scaled, polygon.StrokeThickness * Zoom);
        }
        else
        {
            polygon.FreezeClipGeometry = null;
        }

        if (polygon.Purpose == PolygonPurpose.HideRow && polygon.Vertices.Count >= 3)
        {
            double seamY = MapToDisplay(polygon.Vertices.Min(v => v.Y), _hiddenYRanges) * Zoom;
            double x1 = MapToDisplay(0, _hiddenXRanges) * Zoom;
            double x2 = MapToDisplay(_originalPageWidth, _hiddenXRanges) * Zoom;
            polygon.BreakLinePoints = new PointCollection { new Point(x1, seamY), new Point(x2, seamY) };
        }
        else if (polygon.Purpose == PolygonPurpose.HideColumn && polygon.Vertices.Count >= 3)
        {
            double seamX = MapToDisplay(polygon.Vertices.Min(v => v.X), _hiddenXRanges) * Zoom;
            double y1 = MapToDisplay(0, _hiddenYRanges) * Zoom;
            double y2 = MapToDisplay(_originalPageHeight, _hiddenYRanges) * Zoom;
            polygon.BreakLinePoints = new PointCollection { new Point(seamX, y1), new Point(seamX, y2) };
        }
        else
        {
            polygon.BreakLinePoints = null;
        }
    }

    private void RefreshAllPolygonDisplays()
    {
        foreach (var polygon in Polygons)
            RefreshPolygonDisplay(polygon);
    }

    /// <summary>
    /// Builds a closed clip Geometry from a (zoom-scaled) vertex list, for the Freeze Row pinned
    /// overlay's Border.Clip. Widened by the polygon's own (zoom-scaled) stroke thickness and
    /// unioned with the base shape — WPF strokes render centered on their path by default, so
    /// clipping to the EXACT boundary would cut the outer half of the stroke away, leaving only a
    /// thin half-width line visible. Using GetWidenedPathGeometry (a built-in, well-tested WPF API)
    /// rather than a hand-rolled polygon-offset/inset algorithm keeps this low-risk.
    /// </summary>
    private static Geometry BuildClipGeometry(List<PolygonVertexViewModel> vertices, double strokeThicknessScaled)
    {
        if (vertices.Count < 3) return Geometry.Empty;

        var figure = new PathFigure
        {
            StartPoint = new Point(vertices[0].X, vertices[0].Y),
            IsClosed = true,
            IsFilled = true,
        };
        for (int i = 1; i < vertices.Count; i++)
            figure.Segments.Add(new LineSegment(new Point(vertices[i].X, vertices[i].Y), isStroked: true));

        var baseGeometry = new PathGeometry();
        baseGeometry.Figures.Add(figure);

        var pen = new Pen(Brushes.Black, Math.Max(strokeThicknessScaled, 0) + 2); // +2 safety margin against rounding
        var widened = baseGeometry.GetWidenedPathGeometry(pen);
        return Geometry.Combine(baseGeometry, widened, GeometryCombineMode.Union, null);
    }

    private static List<GeoPoint> ToGeo(IEnumerable<PolygonVertexViewModel> vertices) =>
        vertices.Select(v => new GeoPoint(v.X, v.Y)).ToList();

    /// <summary>The outline of a polygon in PAGE units, for hit-testing: the vertices themselves for a
    /// polygon/rectangle, the flattened true curve for every other kind (so clicking ON a circle's edge
    /// selects it, not just clicking one of its few control points).</summary>
    private static List<PolygonVertexViewModel> GetOutline(PolygonViewModel polygon)
    {
        if (polygon.Kind is ShapeKind.Polygon or ShapeKind.Text or ShapeKind.Image) return polygon.Vertices;
        return ShapeGeometry.Flatten(polygon.Kind, ToGeo(polygon.Vertices), polygon.IsClosed, 0.5)
            .Select(g => new PolygonVertexViewModel { X = g.X, Y = g.Y }).ToList();
    }

    // =====================================================================================================================
    // Pictures
    // =====================================================================================================================
    // A picture is placed like text: one anchor (its top-left), a box rotated about it. Everything it needs is in JobImageStore (the file, kept with the
    // job), ImageGeometry (the tested maths of handles, sizes and mirroring) and the overlay in MainWindow.xaml.

    /// <summary>How far the resize handles stick out past the box, in screen pixels — the grab zone reaches this far too, so a handle can be picked up from
    /// its outer half. A screen constant, so it feels the same at any zoom.</summary>
    private const double ImageHandleReach = 8;

    /// <summary>The widest a picture is decoded for the on-screen overlay. A big photo at full size is a lot of memory and the overlay never needs it;
    /// printing decodes the file at full size.</summary>
    private const int ImageDecodeWidth = 2400;

    private readonly Dictionary<string, BitmapSource?> _imageCache = new();

    private string CurrentJobFolder => CleanPrintPaths.JobFolder(_jobId);

    /// <summary>The decoded picture for a stored file name, loaded once per job and shared by every annotation that uses it (null if the file is gone).</summary>
    private BitmapSource? LoadImageForDisplay(string fileName)
    {
        if (_imageCache.TryGetValue(fileName, out var cached)) return cached;
        var loaded = JobImageStore.Load(CurrentJobFolder, fileName, ImageDecodeWidth);
        _imageCache[fileName] = loaded;
        return loaded;
    }

    /// <summary>Everything the overlay shows for a picture: its picture (loaded on first use) and its box's size at the current zoom. Called from RefreshPolygonDisplay.</summary>
    private void UpdateImageDisplay(PolygonViewModel polygon)
    {
        polygon.ImageDisplayWidth = polygon.ImageWidth * Zoom;
        polygon.ImageDisplayHeight = polygon.ImageHeight * Zoom;
        if (polygon.ImageSource is null && !string.IsNullOrEmpty(polygon.ImageFile) && !string.IsNullOrEmpty(_jobId))
            polygon.ImageSource = LoadImageForDisplay(polygon.ImageFile);
    }

    /// <summary>Whether a point (page units) is on a picture's box. A SELECTED picture also counts a little way outside it, so its resize handles —
    /// which stick out past the box — can be picked up from their outer halves without the click being taken for one on the page.</summary>
    private bool IsPointInImageBox(PolygonViewModel polygon, double x, double y, double tolerance)
    {
        if (polygon.Vertices.Count == 0 || polygon.ImageWidth <= 0 || polygon.ImageHeight <= 0) return false;
        double margin = polygon.IsSelected ? tolerance / 2 + ImageHandleReach / Math.Max(Zoom, 0.01) : 0;
        var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
        return TextGeometry.BoxContains(anchor, polygon.RotationDegrees, polygon.ImageWidth, polygon.ImageHeight, margin, new GeoPoint(x, y));
    }

    /// <summary>The resize handle of a picture that a page point is on, if any (the picture's handles show only while it is the one being edited).</summary>
    public ImageHandle? HitImageHandle(PolygonViewModel polygon, double x, double y)
    {
        if (!polygon.IsImage || polygon.Vertices.Count == 0) return null;
        var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
        var local = TextGeometry.PageToLocal(anchor, polygon.RotationDegrees, new GeoPoint(x, y));
        return ImageGeometry.HitHandle(local, polygon.ImageWidth, polygon.ImageHeight, (ImageHandleReach + 3) / Math.Max(Zoom, 0.01));
    }

    private (GeoPoint Anchor, double Width, double Height)? _imageResizeStart;

    /// <summary>Starts dragging a handle: remembers the box as it is now, so every move is worked out from that, not from the last move (no drift).</summary>
    public void BeginImageResize(PolygonViewModel image)
    {
        if (!image.IsImage || image.Vertices.Count == 0) return;
        _imageResizeStart = (new GeoPoint(image.Vertices[0].X, image.Vertices[0].Y), image.ImageWidth, image.ImageHeight);
    }

    /// <summary>The handle has been dragged to this page point. Not saved until <see cref="EndImageResize"/>.</summary>
    public void ResizeImageTo(PolygonViewModel image, ImageHandle handle, double x, double y)
    {
        if (_imageResizeStart is not { } start || !image.IsImage) return;
        var (anchor, width, height) = ImageGeometry.Resize(start.Anchor, image.RotationDegrees, start.Width, start.Height, handle, new GeoPoint(x, y), KeepImageAspect);
        image.Vertices = new List<PolygonVertexViewModel> { new() { X = anchor.X, Y = anchor.Y } };
        image.ImageWidth = width;
        image.ImageHeight = height;
        RefreshPolygonDisplay(image);
    }

    public void EndImageResize()
    {
        _imageResizeStart = null;
        _ = SavePolygonsForCurrentPageAsync();
    }

    /// <summary>Inserts a picture file onto the current page, in the middle of what is on screen, selected, ready to move and resize.</summary>
    public async Task<bool> InsertImageFileAsync(string sourcePath)
    {
        if (string.IsNullOrEmpty(_jobId) || _currentWhitenedPage is null)
        {
            StatusText = "Open or create a job first: a picture goes onto a page.";
            return false;
        }

        ImportedImage imported;
        string jobFolder = CurrentJobFolder;
        try
        {
            imported = await Task.Run(() => JobImageStore.ImportFile(jobFolder, sourcePath)); // decoding and copying can take a moment for a big photo
        }
        catch (Exception ex)
        {
            StatusText = $"Could not insert that picture: {ex.Message}";
            return false;
        }

        PlaceImage(imported);
        return true;
    }

    /// <summary>Inserts a picture that came from the clipboard (not a file).</summary>
    public bool InsertImageFromBitmap(BitmapSource bitmap)
    {
        if (string.IsNullOrEmpty(_jobId) || _currentWhitenedPage is null) return false;
        try
        {
            PlaceImage(JobImageStore.ImportBitmap(CurrentJobFolder, bitmap));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not insert that picture: {ex.Message}";
            return false;
        }
    }

    private void PlaceImage(ImportedImage imported)
    {
        IsSelectMode = false;
        IsMoveMode = false;
        AbandonCurrentDrawingIfAny();

        // At its natural size on paper, but never bigger than 80% of the page either way.
        double pageWidth = Math.Max(_originalPageWidth, 100), pageHeight = Math.Max(_originalPageHeight, 100);
        var (width, height) = ImageGeometry.FitInside(imported.WidthPx, imported.HeightPx, imported.Dpi, pageWidth * 0.8, pageHeight * 0.8);

        // Centred on what is on screen, but kept on the page.
        var at = PasteTextLocationProvider?.Invoke() ?? new Point(pageWidth / 2, pageHeight / 2);
        double centreX = Math.Clamp(at.X, 0, pageWidth), centreY = Math.Clamp(at.Y, 0, pageHeight);

        var polygon = new PolygonViewModel
        {
            Kind = ShapeKind.Image,
            ImageFile = imported.FileName,
            ImageWidth = width,
            ImageHeight = height,
            FillOpacity = 100,
            IsClosed = false,
            HasStroke = false,     // nothing of a picture is an outline: the overlay draws the picture, and its selection box on top
            IncludeInPrint = true, // a picture you place is meant to be part of the page; untick it to keep it as a preview-only aid
            Vertices = new List<PolygonVertexViewModel> { new() { X = centreX - width / 2, Y = centreY - height / 2 } },
        };

        foreach (var existing in Polygons) existing.IsSelected = false;
        polygon.IsSelected = true;
        Polygons.Add(polygon);
        RefreshPolygonDisplay(polygon);
        UpdateEditablePolygonFlags();
        _ = SavePolygonsForCurrentPageAsync();
        StatusText = "Picture inserted — drag it to move it, drag a square to resize it. Rotation and the aspect-ratio lock are in the bar at the top.";
    }

    // ---- Text annotations: layout, display, hit-testing ----

    /// <summary>How far outside its text box (in SCREEN pixels) the dashed selection rectangle is drawn — and so also how
    /// far the width handle and the grab zone reach past the box. A screen constant, so it looks the same at any zoom.</summary>
    private const double TextBoxOutset = 4;

    /// <summary>How far a text's frame reaches beyond its text box, in page units: the padding plus half the line's width. 0 when there is no frame. The box you can click, and the width handle, move out with it.</summary>
    private static double FrameOutsetDiu(PolygonViewModel p) => p.FrameVisible ? TextGeometry.PointsToDiu(p.FramePaddingPoints) + p.FrameThickness / 2 : 0;

    /// <summary>The kernel radius, in page units, of the blur behind text. Small on purpose ("a little blur"): enough to take
    /// the edge off busy graphics under the text without making the page look smeared. (The Gaussian's sigma is about a third
    /// of it.) The in-place editor strengthens it by <see cref="EditingBlurFactor"/>.</summary>
    private const double BackdropBlurRadiusDiu = 8;
    private const double EditingBlurFactor = 1.7;

    /// <summary>Width of a text outline in page units for a given Thickness-box value. Text is small, so the shapes' 1-10 would be
    /// enormous on it: each step is half a page unit (1 -> 0.5, 4 -> 2). Scales with the page, unlike a shape's outline, so the
    /// outline keeps its proportion to the letters at any zoom and in print.</summary>
    private static double TextOutlineWidthDiu(double thickness) => thickness * 0.5;

    private static TextRenderStyle StyleOf(PolygonViewModel p) => new(
        p.Text, p.FontFamilyName, TextGeometry.PointsToDiu(p.FontSizePoints), p.Bold, p.Italic, p.Underline,
        TextGeometry.PointsToDiu(p.CharacterSpacingPoints), p.LineSpacing, TextGeometry.PointsToDiu(p.BoxWidthPoints), p.Alignment);

    private static TextRenderStyle StyleOf(PolygonAnnotation p) => new(
        p.Text ?? "", p.FontFamily, TextGeometry.PointsToDiu(p.FontSizePoints ?? TextGeometry.DiuToPoints(p.FontSize)), p.Bold, p.Italic, p.Underline,
        TextGeometry.PointsToDiu(p.CharacterSpacingPoints), p.LineSpacing, TextGeometry.PointsToDiu(p.BoxWidthPoints), p.Alignment);

    /// <summary>
    /// Lays a text annotation out and fills in everything the overlay shows for it: the text as an opacity mask (the colour is a
    /// separate fill, so it can change without re-laying anything out), its outline, the box's on-screen size, and the blurred halo —
    /// a mask, rotated with the text, that decides where and how strongly the blurred copy of the page shows through. Called from
    /// RefreshPolygonDisplay, so it follows zoom and every formatting change.
    /// </summary>
    private void UpdateTextDisplay(PolygonViewModel polygon)
    {
        double zoom = Zoom;
        var style = StyleOf(polygon);
        var result = TextRenderer.Render(style, Brushes.Black, null); // the letters, opaque black — used as an opacity MASK; the real colour is a fill behind it

        polygon.TextLayoutWidth = result.Layout.Width;
        polygon.TextLayoutHeight = result.Layout.Height;
        polygon.TextDisplayWidth = result.Layout.Width * zoom;
        polygon.TextDisplayHeight = result.Layout.Height * zoom;
        polygon.DisplayFontSize = TextGeometry.PointsToDiu(polygon.FontSizePoints) * zoom;
        polygon.DisplayLineHeight = result.Layout.LineHeight * zoom;
        polygon.FrameDisplayThickness = polygon.FrameThickness * zoom;
        polygon.FrameDisplayRadius = TextGeometry.PointsToDiu(polygon.FrameCornerRadiusPoints) * zoom;
        polygon.FrameDisplayPad = TextGeometry.PointsToDiu(polygon.FramePaddingPoints) * zoom;

        var scaled = new DrawingGroup { Transform = new ScaleTransform(zoom, zoom) };
        scaled.Children.Add(result.Drawing);
        polygon.TextMask = new DrawingBrush(scaled) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };

        // The letters' shapes as geometry, at the current zoom: stroked for the text's outline, and stroked much wider (and
        // fainter) for the blurred halo. Only built if one of those wants it.
        bool hasText = style.Text.Trim().Length > 0;
        Geometry? letters = null;
        if (polygon.HasStroke || (polygon.BlurBackground && hasText))
        {
            letters = TextRenderer.BuildOutline(style).Clone();
            letters.Transform = new ScaleTransform(zoom, zoom);
            letters.Freeze();
        }

        if (polygon.HasStroke)
        {
            polygon.TextOutline = letters;
            polygon.TextStrokeDisplayThickness = Math.Max(0.5, TextOutlineWidthDiu(polygon.StrokeThickness) * zoom);
        }
        else
        {
            polygon.TextOutline = null;
        }

        if (polygon.DisplayVertices.Count == 0) return;
        var anchor = polygon.DisplayVertices[0];
        double area = TextGeometry.PointsToDiu(polygon.BlurAreaPoints) * zoom;
        var placement = Matrix.Identity;
        placement.Rotate(polygon.RotationDegrees);   // about the box's top-left, which is the mask's origin...
        placement.Translate(anchor.X, anchor.Y);     // ...then moved to where the text is

        // An empty text (just placed, still being typed into) has no letters to put a halo round yet: use its box, so the
        // blur is there from the first moment.
        Geometry haloSource = letters ?? new RectangleGeometry(new Rect(0, 0, polygon.TextDisplayWidth, polygon.TextDisplayHeight));
        polygon.BlurMask = CreateHaloMask(haloSource, polygon.TextDisplayWidth, polygon.TextDisplayHeight, area,
            TextGeometry.FeatherWidth(area, polygon.BlurSoftnessPercent), placement);

        double radius = BackdropBlurRadiusDiu * zoom * (polygon.IsInlineEditing ? EditingBlurFactor : 1.0);
        polygon.BackdropBlurEffect = new BlurEffect
        {
            Radius = Math.Clamp(radius, 0.5, 100),
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
    }

    /// <summary>
    /// Builds the opacity mask for a text's blurred halo: the area within <paramref name="area"/> of the LETTERS (not of their
    /// bounding box), fading out over the last <paramref name="feather"/> of that distance. A fade can't be drawn as a single
    /// stroke, so it is a stack of strokes round the letters — widest and faintest first, each narrower one adding to the last —
    /// whose combined opacity at any distance is <see cref="TextGeometry.HaloStrength"/> (see <see cref="TextGeometry.HaloRings"/>
    /// for how the opacities are chosen to compose to that). The bands are cut about a pixel and a half wide, so their steps cannot
    /// be seen. With no feather it is one sharp stroke. Everything is in screen units with the box's top-left at the origin;
    /// <paramref name="placement"/> rotates and moves it to where the text is.
    /// </summary>
    private static Brush CreateHaloMask(Geometry letters, double boxWidth, double boxHeight, double area, double feather, Matrix placement)
    {
        var outer = new Rect(-area, -area, boxWidth + 2 * area, boxHeight + 2 * area);
        var group = new DrawingGroup();

        int bands = Math.Clamp((int)Math.Round(feather / 1.5), 4, 24);
        foreach (var (radius, alpha) in TextGeometry.HaloRings(area, feather, bands))
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * alpha), 0, 0, 0));
            brush.Freeze();
            var pen = new Pen(brush, 2 * radius) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            // Fill AND stroke: the stroke reaches `radius` beyond the letters' edges; the fill is what covers their insides.
            group.Children.Add(new GeometryDrawing(brush, pen, letters));
        }

        return new DrawingBrush(group)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = outer,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = outer,
            Stretch = Stretch.Fill, TileMode = TileMode.None,
            Transform = new MatrixTransform(placement),
        };
    }

    /// <summary>Whether a click (page units) is on a text annotation's box: the click is turned into the text's own
    /// (rotated) frame and tested against its wrapped size, grown by a small grab margin.</summary>
    private bool IsPointInTextBounds(PolygonViewModel polygon, double x, double y, double tolerance)
    {
        if (polygon.Vertices.Count == 0) return false;
        var layout = TextRenderer.Measure(StyleOf(polygon));
        var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
        double margin = tolerance / 2 + TextBoxOutset / Math.Max(Zoom, 0.01) + FrameOutsetDiu(polygon);
        return TextGeometry.BoxContains(anchor, polygon.RotationDegrees, layout.Width, layout.Height, margin, new GeoPoint(x, y));
    }

    /// <summary>Whether a click is on the right edge of a text box — where the width handle sits. Anywhere along that edge
    /// counts, not just the small square, so the box can be resized from its whole right side.</summary>
    public bool IsOnTextWidthHandle(PolygonViewModel polygon, double x, double y, double tolerance)
    {
        if (!polygon.IsText || polygon.Vertices.Count == 0) return false;
        var layout = TextRenderer.Measure(StyleOf(polygon));
        var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
        var local = TextGeometry.PageToLocal(anchor, polygon.RotationDegrees, new GeoPoint(x, y));
        double edge = layout.Width + TextBoxOutset / Math.Max(Zoom, 0.01) + FrameOutsetDiu(polygon);
        return Math.Abs(local.X - edge) <= tolerance * 0.6 && local.Y >= -tolerance / 2 && local.Y <= layout.Height + tolerance / 2;
    }

    /// <summary>Drags a text box's right edge to a point: the box becomes as wide as that point is along the text's own
    /// direction (so it works at any rotation), never narrower than two characters. Not saved until <see cref="EndTextWidthDrag"/>.</summary>
    public void DragTextWidthTo(PolygonViewModel polygon, double x, double y)
    {
        if (!polygon.IsText || polygon.Vertices.Count == 0) return;
        var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
        var local = TextGeometry.PageToLocal(anchor, polygon.RotationDegrees, new GeoPoint(x, y));
        double widthDiu = local.X - TextBoxOutset / Math.Max(Zoom, 0.01) - FrameOutsetDiu(polygon);
        double minimum = TextGeometry.PointsToDiu(polygon.FontSizePoints) * 2;

        polygon.BoxWidthPoints = TextGeometry.DiuToPoints(Math.Max(widthDiu, minimum));
        RefreshPolygonDisplay(polygon);
        OnPropertyChanged(nameof(CurrentTextBoxWidth));
    }

    public void EndTextWidthDrag() => _ = SavePolygonsForCurrentPageAsync();

    /// <summary>Builds the PointCollection used for a Polyline's rendering — appends the first
    /// vertex again at the end when closed, so the line visually connects back to form a loop.</summary>
    private static PointCollection BuildRenderPoints(List<PolygonVertexViewModel> vertices, bool isClosed)
    {
        var points = new PointCollection(vertices.Select(v => new Point(v.X, v.Y)));
        if (isClosed && vertices.Count >= 3)
            points.Add(new Point(vertices[0].X, vertices[0].Y));
        return points;
    }

    /// <summary>Loads whichever polygons were saved for the current page + current Before/After
    /// variant, discarding any in-progress (unsaved) edit — called on page change and on
    /// Before/After toggle so the right set of polygons is always shown for what's on screen.
    /// Loads every polygon FIRST (without computing its display yet), then recomputes hidden
    /// ranges from the complete set, THEN refreshes each polygon's display — since that display
    /// (for every polygon, not just Hide ones) needs to know about ALL currently-hidden bands to
    /// correctly remap its on-screen position.</summary>
    private void RefreshPolygonsForCurrentPageAndVariant()
    {
        Polygons.Clear();
        _drawingPolygon = null;
        _polygonUndoStack.Clear();

        int pageNumber = CurrentPageIndex + 1;
        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;

        if (_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants) &&
            variants.TryGetValue(variant, out var pageAnnotations))
        {
            foreach (var saved in pageAnnotations.Polygons)
                Polygons.Add(PolygonFromAnnotation(saved));
        }

        RecomputeHiddenBands();
        foreach (var polygon in Polygons)
            RefreshPolygonDisplay(polygon);

        UpdateEditablePolygonFlags();
        EnsureHistoryBaseline();
    }

    /// <summary>Turns a saved annotation into the view-model the overlay and toolbar work with. Shared by loading a page, undo/redo
    /// and paste — one place, so they can't drift apart.</summary>
    private PolygonViewModel PolygonFromAnnotation(PolygonAnnotation saved)
    {
        var polygon = new PolygonViewModel
        {
            Id = saved.Id,
            Kind = saved.Kind,
            Text = saved.Text ?? "",
            // Annotations from before text sizes were points stored page units in FontSize; 1pt = 96/72 units.
            FontSizePoints = saved.FontSizePoints ?? TextGeometry.DiuToPoints(saved.FontSize),
            FontFamilyName = saved.FontFamily,
            Alignment = saved.Alignment,
            Bold = saved.Bold,
            Italic = saved.Italic,
            Underline = saved.Underline,
            RotationDegrees = saved.RotationDegrees,
            CharacterSpacingPoints = saved.CharacterSpacingPoints,
            LineSpacing = saved.LineSpacing,
            BoxWidthPoints = saved.BoxWidthPoints,
            BlurBackground = saved.BlurBackground,
            BlurAreaPoints = saved.BlurAreaPoints,
            BlurSoftnessPercent = saved.BlurSoftnessPercent,
            FrameVisible = saved.FrameVisible,
            FrameStrokeColor = HexToColor(saved.FrameStrokeColorHex),
            FrameThickness = saved.FrameThickness,
            FrameLineStyle = saved.FrameLineStyle,
            FrameFillColor = string.IsNullOrEmpty(saved.FrameFillColorHex) ? null : HexToColor(saved.FrameFillColorHex),
            FrameOpacity = saved.FrameOpacity,
            FramePaddingPoints = saved.FramePaddingPoints,
            FrameCornerRadiusPoints = saved.FrameCornerRadiusPoints,
            Vertices = saved.Vertices.Select(v => new PolygonVertexViewModel { X = v.X, Y = v.Y }).ToList(),
            IsClosed = saved.IsClosed,
            Purpose = saved.Purpose,
            StrokeColor = HexToColor(saved.StrokeColorHex),
            StrokeThickness = saved.StrokeThickness,
            HasStroke = saved.HasStroke,
            FillColor = string.IsNullOrEmpty(saved.FillColorHex) ? null : HexToColor(saved.FillColorHex),
            FillOpacity = saved.FillOpacity,
            IncludeInPrint = saved.IncludeInPrint,
            ImageFile = saved.ImageFile,
            ImageWidth = saved.ImageWidth,
            ImageHeight = saved.ImageHeight,
            FlipHorizontal = saved.FlipHorizontal,
            FlipVertical = saved.FlipVertical,
        };

        if (saved.Kind == ShapeKind.Text && saved.TextStyleVersion < 1)
        {
            // Text from before it had its own fill/stroke/opacity: its colour was the stroke colour. Keep it looking the same —
            // that colour becomes the letters' fill, with no outline and full opacity.
            polygon.FillColor = HexToColor(saved.StrokeColorHex);
            polygon.HasStroke = false;
            polygon.FillOpacity = 100;
        }
        EnsureVisible(polygon); // repairs one saved with neither outline nor fill, which had vanished from the page and left the Color box blank whenever it was selected
        return polygon;
    }

    /// <summary>The inverse of <see cref="PolygonFromAnnotation"/>.</summary>
    private static PolygonAnnotation AnnotationFromPolygon(PolygonViewModel p) => new()
    {
        Id = p.Id,
        Kind = p.Kind,
        Text = p.IsText ? p.Text : null,
        FontSizePoints = p.FontSizePoints,
        FontSize = TextGeometry.PointsToDiu(p.FontSizePoints), // the same size in the old unit, so an older version still reads it correctly
        FontFamily = p.FontFamilyName,
        Alignment = p.Alignment,
        Bold = p.Bold,
        Italic = p.Italic,
        Underline = p.Underline,
        RotationDegrees = p.RotationDegrees,
        CharacterSpacingPoints = p.CharacterSpacingPoints,
        LineSpacing = p.LineSpacing,
        BoxWidthPoints = p.BoxWidthPoints,
        BlurBackground = p.BlurBackground,
        BlurAreaPoints = p.BlurAreaPoints,
        BlurSoftnessPercent = p.BlurSoftnessPercent,
        FrameVisible = p.FrameVisible,
        FrameStrokeColorHex = $"#{p.FrameStrokeColor.R:X2}{p.FrameStrokeColor.G:X2}{p.FrameStrokeColor.B:X2}",
        FrameThickness = p.FrameThickness,
        FrameLineStyle = p.FrameLineStyle,
        FrameFillColorHex = p.FrameFillColor is { } frameFill ? $"#{frameFill.R:X2}{frameFill.G:X2}{frameFill.B:X2}" : null,
        FrameOpacity = p.FrameOpacity,
        FramePaddingPoints = p.FramePaddingPoints,
        FrameCornerRadiusPoints = p.FrameCornerRadiusPoints,
        TextStyleVersion = p.IsText ? 1 : 0,
        Vertices = p.Vertices.Select(v => new PolygonPoint { X = v.X, Y = v.Y }).ToList(),
        IsClosed = p.IsClosed,
        Purpose = p.Purpose,
        StrokeColorHex = $"#{p.StrokeColor.R:X2}{p.StrokeColor.G:X2}{p.StrokeColor.B:X2}",
        StrokeThickness = p.StrokeThickness,
        HasStroke = p.HasStroke,
        FillColorHex = p.FillColor is { } fc ? $"#{fc.R:X2}{fc.G:X2}{fc.B:X2}" : null,
        FillOpacity = p.FillOpacity,
        IncludeInPrint = p.IncludeInPrint,
        ImageFile = p.IsImage ? p.ImageFile : null,
        ImageWidth = p.IsImage ? p.ImageWidth : 0,
        ImageHeight = p.IsImage ? p.ImageHeight : 0,
        FlipHorizontal = p.IsImage && p.FlipHorizontal,
        FlipVertical = p.IsImage && p.FlipVertical,
    };

    /// <summary>Writes the current Polygons collection back into _jobAnnotationSettings for the
    /// current page + variant, then persists the whole job's annotation settings to disk.</summary>
    private async Task SavePolygonsForCurrentPageAsync()
    {
        if (string.IsNullOrEmpty(_jobId)) return;

        int pageNumber = CurrentPageIndex + 1;
        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;

        if (!_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants))
        {
            variants = new Dictionary<ImageVariant, PageAnnotations>();
            _jobAnnotationSettings.Pages[pageNumber] = variants;
        }

        var snapshot = Polygons
            .Where(p => !ReferenceEquals(p, _drawingPolygon)) // in-progress drawing isn't saved until Finish/Close
            .Select(AnnotationFromPolygon)
            .ToList();
        variants[variant] = new PageAnnotations { Polygons = snapshot };
        RecordHistory(snapshot);

        await _polygonStore.SaveAsync(_jobId, _jobAnnotationSettings);
        StatusText = $"Polygons saved for page {pageNumber} ({(ShowBefore ? "uncleaned" : "cleaned")}).";
    }

    // =====================================================================================================================
    // Undo / redo of annotation edits
    // =====================================================================================================================
    // Every change to a page's annotations — adding, deleting, moving, restyling, editing text — already ends in
    // SavePolygonsForCurrentPageAsync, so that is where history is recorded: the page's annotations are serialized and kept as one
    // "state". Undo/redo step between states and write the chosen one back through the normal load path, so there is no second
    // code path that could disagree with it.
    //
    // History is per page (and per Before/After variant): undoing on page 2 never touches page 1. A continuous gesture saves on
    // every tick (a drag autosaves as it goes, as does typing in a number box), so saves within a short window of each other
    // replace the latest state instead of piling up — one drag is one undo step. What isn't covered: things that aren't annotations
    // (effects, rotation, job changes). While a polygon or curve is being drawn, undo takes back its last point instead, as before.

    private sealed class PageHistory
    {
        public List<string> States { get; } = new();
        public int Index { get; set; }
        public DateTime LastRecordedUtc { get; set; } = DateTime.MinValue;
    }

    private readonly Dictionary<string, PageHistory> _histories = new();
    private bool _applyingHistory;
    private const int MaxHistoryStates = 100;
    private static readonly TimeSpan HistoryCoalesceWindow = TimeSpan.FromMilliseconds(600);

    private string HistoryKey => $"{_jobId}|{CurrentPageIndex + 1}|{(ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned)}";
    private PageHistory? CurrentHistory => _histories.TryGetValue(HistoryKey, out var history) ? history : null;

    private static string SerializeState(List<PolygonAnnotation> annotations) => JsonSerializer.Serialize(annotations);

    /// <summary>Makes sure the page on screen has a history, starting from how it was loaded — so the first edit has something to go back to.</summary>
    private void EnsureHistoryBaseline()
    {
        if (string.IsNullOrEmpty(_jobId)) return;
        if (!_histories.ContainsKey(HistoryKey))
        {
            var history = new PageHistory();
            history.States.Add(SerializeState(Polygons.Select(AnnotationFromPolygon).ToList()));
            _histories[HistoryKey] = history;
        }
        RaiseUndoRedoChanged();
    }

    private void RecordHistory(List<PolygonAnnotation> snapshot)
    {
        if (_applyingHistory || string.IsNullOrEmpty(_jobId)) return;

        if (!_histories.TryGetValue(HistoryKey, out var history))
            _histories[HistoryKey] = history = new PageHistory();

        string state = SerializeState(snapshot);
        if (history.States.Count > 0 && history.States[history.Index] == state) return; // nothing actually changed

        // A new edit ends the redo branch.
        if (history.Index < history.States.Count - 1)
            history.States.RemoveRange(history.Index + 1, history.States.Count - history.Index - 1);

        var now = DateTime.UtcNow;
        if (history.Index > 0 && now - history.LastRecordedUtc < HistoryCoalesceWindow)
        {
            history.States[history.Index] = state; // still the same gesture
        }
        else
        {
            history.States.Add(state);
            history.Index = history.States.Count - 1;
            if (history.States.Count > MaxHistoryStates)
            {
                history.States.RemoveAt(0);
                history.Index--;
            }
        }
        history.LastRecordedUtc = now;
        RaiseUndoRedoChanged();
    }

    private bool CanUndoNow => IsDrawingPolygon ? _polygonUndoStack.Count > 0 : CurrentHistory is { Index: > 0 };
    private bool CanRedoNow => !IsDrawingPolygon && CurrentHistory is { } history && history.Index < history.States.Count - 1;

    private void RaiseUndoRedoChanged()
    {
        // Null-safe: this is also reached while the view-model is still being constructed.
        UndoCommand?.RaiseCanExecuteChanged();
        RedoCommand?.RaiseCanExecuteChanged();
        MirrorHorizontalCommand?.RaiseCanExecuteChanged(); // selection changes arrive here too, and these depend on the selection
        MirrorVerticalCommand?.RaiseCanExecuteChanged();
    }

    private async Task UndoAsync()
    {
        if (IsDrawingPolygon)
        {
            UndoPolygonEdit(); // mid-drawing: take back the last point
            RaiseUndoRedoChanged();
            return;
        }
        await StepHistoryAsync(-1);
    }

    private static bool HasHide(IEnumerable<PolygonAnnotation> annotations) =>
        annotations.Any(a => a.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn);

    private async Task StepHistoryAsync(int delta)
    {
        if (IsDrawingPolygon || CurrentHistory is not { } history) return;
        int target = history.Index + delta;
        if (target < 0 || target >= history.States.Count) return;

        var restored = JsonSerializer.Deserialize<List<PolygonAnnotation>>(history.States[target]) ?? new List<PolygonAnnotation>();
        bool involvesHide = HasHide(restored) || Polygons.Any(p => p.Purpose is PolygonPurpose.HideRow or PolygonPurpose.HideColumn);
        history.Index = target;

        int pageNumber = CurrentPageIndex + 1;
        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;
        if (!_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants))
            _jobAnnotationSettings.Pages[pageNumber] = variants = new Dictionary<ImageVariant, PageAnnotations>();
        variants[variant] = new PageAnnotations { Polygons = restored };

        _applyingHistory = true; // the reload below must not record itself as a new edit
        try
        {
            await _polygonStore.SaveAsync(_jobId, _jobAnnotationSettings);
            RefreshPolygonsForCurrentPageAndVariant();
            if (involvesHide) RefreshHiddenBandsAndRecomposite(); // a hide that was undone/redone changes the page image itself
        }
        finally
        {
            _applyingHistory = false;
        }
        RaiseUndoRedoChanged();
        StatusText = delta < 0 ? "Undone." : "Redone.";
    }

    // =====================================================================================================================
    // Copy / cut / paste
    // =====================================================================================================================
    // Selected annotations travel on the clipboard as JSON in a CleanPrint-specific format (so they paste back with every property
    // intact — text, curves, circles, styling), plus plain text for any text they contain, so a label can be pasted into other
    // programs. Pasting plain text from elsewhere makes a new text annotation. A pasted copy is offset so it doesn't hide the
    // original (more on each repeat), unless it's going onto a different page. Freeze/Hide purposes are NOT copied: a second copy
    // of a hidden row would hide it twice.

    private const string ClipboardFormat = "CleanPrint.Annotations.v1";
    private const double PasteOffsetDiu = 18;
    private int _pasteCount;

    private sealed class ClipboardPayload
    {
        public string? SourceJobId { get; set; }
        public int SourcePage { get; set; }
        public ImageVariant SourceVariant { get; set; }
        public List<PolygonAnnotation> Polygons { get; set; } = new();
    }

    /// <summary>Supplied by the window: where, in page units, a pasted line of plain text should land (the middle of what is on screen).</summary>
    public Func<Point?>? PasteTextLocationProvider { get; set; }

    private bool HasPastableSelection => !IsDrawingPolygon && Polygons.Any(p => p.IsSelected);

    private bool CanPasteNow
    {
        get
        {
            if (IsDrawingPolygon || string.IsNullOrEmpty(_jobId)) return false;
            try { return Clipboard.ContainsData(ClipboardFormat) || Clipboard.ContainsText() || Clipboard.ContainsImage(); }
            catch (System.Runtime.InteropServices.COMException) { return false; } // the clipboard is momentarily owned by another program
        }
    }

    public void CopySelection()
    {
        var selected = Polygons.Where(p => p.IsSelected && !ReferenceEquals(p, _drawingPolygon)).ToList();
        if (selected.Count == 0) return;

        var payload = new ClipboardPayload
        {
            SourceJobId = _jobId,
            SourcePage = CurrentPageIndex + 1,
            SourceVariant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned,
            Polygons = selected.Select(AnnotationFromPolygon).ToList(),
        };

        var data = new DataObject();
        data.SetData(ClipboardFormat, JsonSerializer.Serialize(payload));
        var texts = selected.Where(p => p.IsText && p.Text.Length > 0).Select(p => p.Text).ToList();
        if (texts.Count > 0) data.SetText(string.Join(Environment.NewLine, texts));

        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "Couldn't copy: the clipboard is in use by another program. Try again.";
            return;
        }
        _pasteCount = 0;
        StatusText = selected.Count == 1 ? "Copied 1 item." : $"Copied {selected.Count} items.";
    }

    public void CutSelection()
    {
        if (!HasPastableSelection) return;
        CopySelection();
        DeleteSelectedPolygonsCommand.Execute(null);
    }

    public void PasteFromClipboard()
    {
        if (IsDrawingPolygon || string.IsNullOrEmpty(_jobId)) return;

        string? json = null, text = null;
        BitmapSource? picture = null;
        try
        {
            if (Clipboard.ContainsData(ClipboardFormat)) json = Clipboard.GetData(ClipboardFormat) as string;
            else if (Clipboard.ContainsText()) text = Clipboard.GetText();   // text first: copying cells from a spreadsheet puts a picture of them on the clipboard too
            else if (Clipboard.ContainsImage()) picture = Clipboard.GetImage();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "Couldn't paste: the clipboard is in use by another program. Try again.";
            return;
        }

        if (!string.IsNullOrEmpty(json)) PasteShapes(json);
        else if (!string.IsNullOrWhiteSpace(text)) PasteText(text);
        else if (picture is not null) InsertImageFromBitmap(picture);
    }

    private void PasteShapes(string json)
    {
        ClipboardPayload? payload;
        try { payload = JsonSerializer.Deserialize<ClipboardPayload>(json); }
        catch (JsonException) { return; }
        if (payload is null || payload.Polygons.Count == 0) return;

        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;
        bool samePage = payload.SourceJobId == _jobId && payload.SourcePage == CurrentPageIndex + 1 && payload.SourceVariant == variant;
        _pasteCount++;
        double offset = samePage ? PasteOffsetDiu * _pasteCount : PasteOffsetDiu * (_pasteCount - 1);

        foreach (var existing in Polygons) existing.IsSelected = false;
        foreach (var saved in payload.Polygons)
        {
            saved.Id = Guid.NewGuid();
            saved.Purpose = PolygonPurpose.None;
            foreach (var vertex in saved.Vertices) { vertex.X += offset; vertex.Y += offset; }

            // A picture lives in its own job's folder: bring the file along when it is pasted into a different job.
            if (saved.Kind == ShapeKind.Image && !string.IsNullOrEmpty(saved.ImageFile) && !string.IsNullOrEmpty(payload.SourceJobId) && payload.SourceJobId != _jobId)
                JobImageStore.CopyAsset(CleanPrintPaths.JobFolder(payload.SourceJobId), CurrentJobFolder, saved.ImageFile);

            var polygon = PolygonFromAnnotation(saved);
            polygon.IsSelected = true;
            Polygons.Add(polygon);
            RefreshPolygonDisplay(polygon);
        }

        UpdateEditablePolygonFlags();
        _ = SavePolygonsForCurrentPageAsync();
        StatusText = payload.Polygons.Count == 1 ? "Pasted 1 item." : $"Pasted {payload.Polygons.Count} items.";
    }

    private void PasteText(string text)
    {
        var at = PasteTextLocationProvider?.Invoke() ?? new Point(40, 40);
        var polygon = new PolygonViewModel { Kind = ShapeKind.Text };
        ApplyDefaultTextStyle(polygon);
        polygon.Text = text.Trim();
        polygon.Vertices = new List<PolygonVertexViewModel> { new() { X = at.X, Y = at.Y } };

        foreach (var existing in Polygons) existing.IsSelected = false;
        polygon.IsSelected = true;
        Polygons.Add(polygon);
        RefreshPolygonDisplay(polygon);
        UpdateEditablePolygonFlags();
        _ = SavePolygonsForCurrentPageAsync();
        StatusText = "Pasted text.";
    }

    // =====================================================================================================================
    // Selecting with a rectangle, and moving the selection
    // =====================================================================================================================

    private bool _isSelectMode;
    /// <summary>While on, dragging across empty space draws a selection rectangle (instead of panning the page). Holding Shift does the
    /// same in any mode. Exclusive with <see cref="IsMoveMode"/>.</summary>
    public bool IsSelectMode
    {
        get => _isSelectMode;
        set { if (SetField(ref _isSelectMode, value) && value) IsMoveMode = false; }
    }

    private bool _isMoveMode;
    /// <summary>
    /// The Move command. While on, dragging any annotation by its edge (or, for filled shapes and text, anywhere on it) moves it
    /// without selecting it first — and if it is part of the current selection, the whole selection moves with it. Dragging empty
    /// space with something selected moves the selection too. Exclusive with <see cref="IsSelectMode"/>.
    /// </summary>
    public bool IsMoveMode
    {
        get => _isMoveMode;
        set { if (SetField(ref _isMoveMode, value) && value) IsSelectMode = false; }
    }

    /// <summary>The page-space bounding box of an annotation as drawn (its outline, or its rotated text box); null if it has no points.</summary>
    private (double MinX, double MinY, double MaxX, double MaxY)? GetEntityBounds(PolygonViewModel polygon)
    {
        if (polygon.Vertices.Count == 0) return null;
        if (polygon.IsText)
        {
            var layout = TextRenderer.Measure(StyleOf(polygon));
            var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
            return TextGeometry.BoundsOf(TextGeometry.RectangleCorners(anchor, polygon.RotationDegrees, 0, 0, layout.Width, layout.Height));
        }
        if (polygon.IsImage)
        {
            var anchor = new GeoPoint(polygon.Vertices[0].X, polygon.Vertices[0].Y);
            return TextGeometry.BoundsOf(TextGeometry.RectangleCorners(anchor, polygon.RotationDegrees, 0, 0, polygon.ImageWidth, polygon.ImageHeight));
        }
        return TextGeometry.BoundsOf(GetOutline(polygon).Select(v => new GeoPoint(v.X, v.Y)));
    }

    /// <summary>
    /// Selects every annotation whose bounds touch the rectangle (page units, any two opposite corners). With
    /// <paramref name="addToSelection"/> it adds to the current selection instead of replacing it. A Hide Row/Column isn't picked up:
    /// once hidden its outline has collapsed into a seam, so a rectangle drawn over the page doesn't meaningfully cover it (click it).
    /// Returns how many are selected afterwards.
    /// </summary>
    public int SelectInRectangle(double x1, double y1, double x2, double y2, bool addToSelection)
    {
        double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2), minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
        if (!addToSelection)
            foreach (var polygon in Polygons) polygon.IsSelected = false;

        foreach (var polygon in Polygons)
        {
            if (ReferenceEquals(polygon, _drawingPolygon) || polygon.IsHidden) continue;
            if (GetEntityBounds(polygon) is { } b && b.MaxX >= minX && b.MinX <= maxX && b.MaxY >= minY && b.MinY <= maxY)
                polygon.IsSelected = true;
        }
        UpdateEditablePolygonFlags();
        return Polygons.Count(p => p.IsSelected);
    }

    /// <summary>Says how many items a rectangle selection picked up, in the title bar's status message.</summary>
    public void ReportSelection(int count) =>
        StatusText = count == 0 ? "Nothing selected." : count == 1 ? "1 item selected." : $"{count} items selected.";

    public void SelectAllPolygons()
    {
        foreach (var polygon in Polygons)
            if (!ReferenceEquals(polygon, _drawingPolygon)) polygon.IsSelected = true;
        UpdateEditablePolygonFlags();
    }

    private bool _selectionMoved;

    /// <summary>Moves every selected annotation by the offset (page units). Called on every tick of a drag; nothing is saved until
    /// <see cref="EndMoveSelected"/>, so the whole drag is one undo step and one write.</summary>
    public void MoveSelectedBy(double dx, double dy)
    {
        foreach (var polygon in Polygons.Where(p => p.IsSelected && !ReferenceEquals(p, _drawingPolygon)))
        {
            polygon.Vertices = polygon.Vertices.Select(v => new PolygonVertexViewModel { X = v.X + dx, Y = v.Y + dy }).ToList();
            RefreshPolygonDisplay(polygon);
            _selectionMoved = true;
        }
    }

    /// <summary>Ends a move: saves, and — if a Hide Row/Column was among what moved — redoes the hidden bands (done once here, not per tick,
    /// for the same reason as in <see cref="EndVertexDrag"/>).</summary>
    public void EndMoveSelected()
    {
        if (!_selectionMoved) return;
        _selectionMoved = false;
        if (Polygons.Any(p => p.IsSelected && p.IsHidden))
            RefreshHiddenBandsAndRecomposite();
        _ = SavePolygonsForCurrentPageAsync();
    }

    // =====================================================================================================================
    // Mirroring
    // =====================================================================================================================
    // Flips the selection left-right ("horizontal": about a vertical line) or top-bottom ("vertical": about a horizontal one). The line is
    // through the MIDDLE OF THE SELECTION AS A GROUP, so the group flips in place — a single shape mirrors about its own centre, several keep
    // their arrangement relative to each other, just reversed. Freeze and Hide bands are left alone: they steer which rows and columns of the page
    // are pinned or hidden, and flipping one would silently change that.

    private bool IsMirrorable(PolygonViewModel p) =>
        p.IsSelected && !ReferenceEquals(p, _drawingPolygon) && p.Purpose == PolygonPurpose.None && p.Vertices.Count > 0;

    private bool HasMirrorableSelection => !IsDrawingPolygon && Polygons.Any(IsMirrorable);

    private void MirrorSelection(bool horizontal)
    {
        var targets = Polygons.Where(IsMirrorable).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Select shapes or text to mirror first. (Freeze and Hide bands can't be mirrored.)";
            return;
        }

        var boxes = targets.Select(GetEntityBounds).Where(b => b is not null).Select(b => b!.Value).ToList();
        double axis = horizontal
            ? (boxes.Min(b => b.MinX) + boxes.Max(b => b.MaxX)) / 2
            : (boxes.Min(b => b.MinY) + boxes.Max(b => b.MaxY)) / 2;

        foreach (var polygon in targets)
        {
            MirrorPolygon(polygon, horizontal, axis);
            RefreshPolygonDisplay(polygon);
        }
        _ = SavePolygonsForCurrentPageAsync(); // one save: the whole mirror is one undo step

        int bandsLeftAlone = Polygons.Count(p => p.IsSelected && !ReferenceEquals(p, _drawingPolygon) && p.Purpose != PolygonPurpose.None);
        StatusText = (horizontal ? "Mirrored horizontally" : "Mirrored vertically") +
                     (bandsLeftAlone > 0 ? $" ({bandsLeftAlone} Freeze/Hide band{(bandsLeftAlone == 1 ? "" : "s")} left as they were)." : ".");
    }

    /// <summary>Reflects one annotation about the line x = axis (horizontal) or y = axis (vertical).</summary>
    private void MirrorPolygon(PolygonViewModel polygon, bool horizontal, double axis)
    {
        PolygonVertexViewModel Reflect(PolygonVertexViewModel v) => horizontal
            ? new PolygonVertexViewModel { X = 2 * axis - v.X, Y = v.Y }
            : new PolygonVertexViewModel { X = v.X, Y = 2 * axis - v.Y };

        if (polygon.IsImage)
        {
            // A picture is MIRRORED, unlike text: its box goes to the mirrored place and the picture's own flip is toggled, so every point of it lands on
            // the mirror image of where it was (ImageGeometry.MirrorPicture, tested point by point).
            var anchor = polygon.Vertices[0];
            var mirrored = ImageGeometry.MirrorPicture(new GeoPoint(anchor.X, anchor.Y), polygon.RotationDegrees, polygon.ImageWidth, polygon.ImageHeight,
                polygon.FlipHorizontal, polygon.FlipVertical, horizontal, axis);
            polygon.Vertices = new List<PolygonVertexViewModel> { new() { X = mirrored.Anchor.X, Y = mirrored.Anchor.Y } };
            polygon.RotationDegrees = mirrored.RotationDegrees;
            polygon.FlipHorizontal = mirrored.FlipHorizontal;
            polygon.FlipVertical = mirrored.FlipVertical;
            return;
        }

        if (polygon.IsText)
        {
            // The letters must stay readable, so the box moves to the mirrored place with its rotation reflected, not the glyphs flipped
            // (see TextGeometry.MirrorTextBox for why that is exactly the mirror image of the region the text covered).
            var layout = TextRenderer.Measure(StyleOf(polygon));
            var anchor = polygon.Vertices[0];
            var (mirroredAnchor, rotation) = TextGeometry.MirrorTextBox(
                new GeoPoint(anchor.X, anchor.Y), polygon.RotationDegrees, layout.Width, layout.Height, horizontal, axis);
            polygon.Vertices = new List<PolygonVertexViewModel> { new() { X = mirroredAnchor.X, Y = mirroredAnchor.Y } };
            polygon.RotationDegrees = rotation;
            return;
        }

        if (polygon.Kind == ShapeKind.Ellipse && polygon.Vertices.Count >= 3)
        {
            // An ellipse is [centre, right-hand extent, bottom extent] with its radii along the axes, so a mirror moves the centre and
            // leaves the radii exactly as they are (reflecting the two extent points directly would put them on the wrong sides).
            var centre = polygon.Vertices[0];
            double radiusX = Math.Abs(polygon.Vertices[1].X - centre.X), radiusY = Math.Abs(polygon.Vertices[2].Y - centre.Y);
            var mirroredCentre = Reflect(centre);
            polygon.Vertices = new List<PolygonVertexViewModel>
            {
                mirroredCentre,
                new() { X = mirroredCentre.X + radiusX, Y = mirroredCentre.Y },
                new() { X = mirroredCentre.X, Y = mirroredCentre.Y + radiusY },
            };
            return;
        }

        // Polygons, rectangles, curves, circles by centre or by three points, arcs: all defined by their points, so reflecting the points reflects the shape.
        polygon.Vertices = polygon.Vertices.Select(Reflect).ToList();
    }

    private static Color HexToColor(string hex)
    {
        try
        {
            var converted = ColorConverter.ConvertFromString(hex);
            return converted is Color c ? c : Colors.Blue;
        }
        catch
        {
            return Colors.Blue;
        }
    }

    /// <summary>Shows a message in the title bar. The StatusText setter is private on purpose (the view model decides what the title says); code outside it, such as App, uses this.</summary>
    public void ShowStatus(string message) => StatusText = message;

    private string _statusText = "Waiting for a print job. Print to \"Clean Print\" from any app.";
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetField(ref _statusText, value))
                OnPropertyChanged(nameof(TitleStatusSuffix));
        }
    }

    /// <summary>The status message as the title bar shows it, right after the app name: " - Loaded page
    /// 1 of 3", or nothing at all while the status is empty (so the title never ends in a dangling
    /// " - ").</summary>
    public string TitleStatusSuffix => string.IsNullOrWhiteSpace(_statusText) ? "" : " - " + _statusText;

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        private set
        {
            if (SetField(ref _currentPageIndex, value))
            {
                OnPropertyChanged(nameof(PageIndicator));
                OnPropertyChanged(nameof(CurrentPageNumber));
            }
        }
    }

    /// <summary>The main window's page box: the 1-BASED number of the page being shown, and — when set —
    /// a jump to that page (clamped to the job's range, so a typo does something sensible). Reads 1
    /// even with nothing loaded, since every job has at least one page; a page box that said 0 or
    /// "No pages loaded" would just be noise. A property setter can't be async, so the jump is
    /// fire-and-forget; the box refreshes itself from CurrentPageIndex once it lands.</summary>
    public int CurrentPageNumber
    {
        get => _pageFilePaths.Count == 0 ? 1 : CurrentPageIndex + 1;
        set => _ = GoToPageAsync(value);
    }

    /// <summary>The total shown after "of" in the page box — never below 1, for the same reason as
    /// CurrentPageNumber. (PageCount stays the true count, which can be 0, for everything else.)</summary>
    public int PageCountDisplay => Math.Max(1, _pageFilePaths.Count);

    /// <summary>Jumps the MAIN window to a page (1-based). Out-of-range values are clamped; jumping to
    /// the page already shown, or with nothing loaded, does nothing. Used by the page box and the
    /// First/Last buttons. (The Print dialog's preview pages independently — GoToPrintPreviewPageAsync.)</summary>
    public async Task GoToPageAsync(int pageNumber1Based)
    {
        if (_pageFilePaths.Count == 0) return;
        int index = Math.Clamp(pageNumber1Based - 1, 0, _pageFilePaths.Count - 1);
        if (index == CurrentPageIndex)
        {
            OnPropertyChanged(nameof(CurrentPageNumber)); // snaps a clamped/typo'd entry in the box back to the real page number
            return;
        }
        await LoadPageAsync(index);
    }

    /// <summary>Total page count in the current job — 0 if none is loaded. Raised explicitly in
    /// LoadJob (not just piggybacked on CurrentPageIndex's own notification) since switching to a
    /// same-length-position job (e.g. both jobs' first page happens to already be index 0) would
    /// otherwise never re-raise it despite the total actually having changed.</summary>
    public int PageCount => _pageFilePaths.Count;

    public string PageIndicator => _pageFilePaths.Count == 0
        ? "No pages loaded"
        : $"Page {CurrentPageIndex + 1} of {_pageFilePaths.Count}";

    private bool CanGoNext => CurrentPageIndex < _pageFilePaths.Count - 1;
    private bool CanGoPrevious => CurrentPageIndex > 0;

    // ---- Print dialog preview ----
    // The Print dialog previews pages on its OWN page index, completely independent of the main
    // window's CurrentPageIndex — paging through the preview must not move the main window's
    // page (and previously did, since both shared one index). It also renders the page the way it
    // will actually print (BuildPrintPageAsync: effects, crop, "Include in Print" polygons, hidden
    // rows/columns removed) rather than reusing the main window's PreviewImage, which has none of
    // the baked-in polygons.

    private int _printPreviewPageIndex;
    private int _printPreviewRenderToken;

    private BitmapSource? _printPreviewImage;
    public BitmapSource? PrintPreviewImage
    {
        get => _printPreviewImage;
        private set => SetField(ref _printPreviewImage, value);
    }

    private bool _isPrintPreviewRendering;
    /// <summary>True while a preview render is in flight — drives a small "Rendering preview..." hint
    /// in the dialog. Deliberately separate from IsBusy: a preview render shouldn't disable the main
    /// window's toolbar or the dialog's own Print button.</summary>
    public bool IsPrintPreviewRendering
    {
        get => _isPrintPreviewRendering;
        private set => SetField(ref _isPrintPreviewRendering, value);
    }

    public RelayCommand PrintPreviewPreviousCommand { get; }
    public RelayCommand PrintPreviewNextCommand { get; }

    // ---- Which pages the preview moves through, and per-page settings ----
    // The preview shows only the pages the print will cover (Page Range: all / current / the ones typed in), and Previous / Next / First / Last move through just those.
    // "Current Page" means the page that was being previewed when it was chosen (or the main window's page, when the dialog is opened).

    private int _printCurrentPageIndex;
    private List<int> _printPreviewPages = new();   // 0-based page indices, in job order
    private readonly Dictionary<int, PagePrintOverride> _pagePrintOverrides = new();

    private PrintRangeKind PrintRange => PageRangeMode switch
    {
        PageRangeMode.Current => PrintRangeKind.Current,
        PageRangeMode.Selected => PrintRangeKind.Selected,
        _ => PrintRangeKind.All,
    };

    /// <summary>Works out which pages the preview covers (from the page range) and keeps it on the same page if that is still among them, else the first. Optionally re-renders.</summary>
    private void RebuildPrintPreviewPages(bool refreshImage)
    {
        _printPreviewPages = PrintPageRange.Resolve(PrintRange, _printCurrentPageIndex, SelectedPagesRangeText, _pageFilePaths.Count);
        int keep = PrintPageRange.KeepOrFirst(_printPreviewPages, _printPreviewPageIndex);
        if (keep >= 0) _printPreviewPageIndex = keep;
        RaisePrintPreviewPagingChanged();
        if (refreshImage) _ = RefreshPrintPreviewAsync();
    }

    private int _rangeRefreshToken;

    /// <summary>Typing in the range box changes it key by key; re-rendering for every key would pile up work, so the picture waits for a pause (the page list itself updates at once).</summary>
    private async void SchedulePrintPreviewRefresh()
    {
        int token = ++_rangeRefreshToken;
        await Task.Delay(350);
        if (token == _rangeRefreshToken) await RefreshPrintPreviewAsync();
    }

    /// <summary>A new job, or a changed page list: start the preview on the first page, with "Current Page" meaning it too.</summary>
    private void ResetPrintPreviewPaging()
    {
        _printPreviewPageIndex = 0;
        _printCurrentPageIndex = 0;
        RebuildPrintPreviewPages(refreshImage: false);
    }

    private void RaisePrintPreviewPagingChanged()
    {
        OnPropertyChanged(nameof(PrintPreviewPageNumber));
        OnPropertyChanged(nameof(PrintPreviewPageCount));
        OnPropertyChanged(nameof(HasPrintPreviewPages));
        OnPropertyChanged(nameof(PrintPreviewStatusText));
        PrintPreviewPreviousCommand?.RaiseCanExecuteChanged();
        PrintPreviewNextCommand?.RaiseCanExecuteChanged();
        RaisePageSettingsOverrideChanged();
    }

    /// <summary>1-based position of the previewed page among the pages being previewed, as shown in (and typed into) the Print dialog's page box. Setting it navigates the PREVIEW only;
    /// the setter can't be async, so rendering is fire-and-forget.</summary>
    public int PrintPreviewPageNumber
    {
        get => PrintPageRange.Position(_printPreviewPages, _printPreviewPageIndex);
        set => _ = GoToPrintPreviewPageAsync(value);
    }

    /// <summary>How many pages the preview moves through: the print's page range, not the whole job.</summary>
    public int PrintPreviewPageCount => _printPreviewPages.Count;

    public bool HasPrintPreviewPages => _printPreviewPages.Count > 0;

    /// <summary>A line under the page box: which page of the JOB is showing when the range narrows it down, or why nothing is.</summary>
    public string PrintPreviewStatusText =>
        _pageFilePaths.Count == 0 ? ""
        : _printPreviewPages.Count == 0 ? "No pages match the page range."
        : _printPreviewPages.Count < _pageFilePaths.Count ? $"This is page {_printPreviewPageIndex + 1} of the job's {_pageFilePaths.Count}."
        : "";

    private void SetPrintPreviewIndex(int pageIndex)
    {
        _printPreviewPageIndex = pageIndex;
        RaisePrintPreviewPagingChanged();
    }

    /// <summary>Points the preview at whichever page the main window is currently showing — called
    /// when the Print dialog is opened, so it starts where the person was looking (or, if that page isn't in the page range,
    /// on the range's first page). Doesn't render by itself (the dialog calls RefreshPrintPreviewAsync once it's actually active).</summary>
    public void SyncPrintPreviewToCurrentPage()
    {
        int page = _pageFilePaths.Count == 0 ? 0 : Math.Clamp(CurrentPageIndex, 0, _pageFilePaths.Count - 1);
        _printCurrentPageIndex = page;
        _printPreviewPageIndex = page;
        RebuildPrintPreviewPages(refreshImage: false);
    }

    /// <summary>Jumps the preview to a position in the page range (1-based, as shown to the person); out-of-range values
    /// are clamped rather than rejected, so a stray typo in the page box does something sensible.</summary>
    public async Task GoToPrintPreviewPageAsync(int position1Based)
    {
        int page = PrintPageRange.AtPosition(_printPreviewPages, position1Based);
        if (page < 0) return;
        SetPrintPreviewIndex(page);
        await RefreshPrintPreviewAsync();
    }

    private async Task StepPrintPreviewAsync(int delta)
    {
        int page = PrintPageRange.Step(_printPreviewPages, _printPreviewPageIndex, delta);
        if (page < 0) return;
        SetPrintPreviewIndex(page);
        await RefreshPrintPreviewAsync();
    }

    // ---- Different page settings for one page (paper size, orientation, scale) ----
    // Ticking "different settings for this page" in the Print dialog gives the PREVIEWED page its own paper size, orientation and scale, copied from the common settings at that
    // moment; the other pages keep the common ones. They go to the printer page by page inside the one print job (PrintingService.QueryPageSettings).

    private PagePrintOverride? PreviewOverride => _pagePrintOverrides.TryGetValue(_printPreviewPageIndex, out var own) ? own : null;

    private PagePrintOverride EffectivePreviewSettings => PagePrintOverride.Effective(BuildPrintOptions(), PreviewOverride);

    // What the preview sheet shows: the previewed page's own settings if it has any.
    public bool PreviewIsLandscape => EffectivePreviewSettings.Orientation == PageOrientation.Landscape;
    public bool PreviewFitToPage => EffectivePreviewSettings.FitToPage;
    public double PreviewScalePercent => EffectivePreviewSettings.ScalePercent;

    public string PageSettingsOverrideHeader => $"Different settings for page {_printPreviewPageIndex + 1}";

    /// <summary>The per-page settings only make sense when there is more than one page to differ between: for a job of one page they are not shown at all.</summary>
    public bool HasMultiplePages => _pageFilePaths.Count > 1;

    private bool _pageSettingsPanelExpanded;
    /// <summary>Whether the per-page settings under the preview are open. Closed by default so the preview has the room; the header says when the previewed page has settings of its own.</summary>
    public bool PageSettingsPanelExpanded
    {
        get => _pageSettingsPanelExpanded;
        set => SetField(ref _pageSettingsPanelExpanded, value);
    }

    /// <summary>The header line of the per-page settings: which page, and whether it has its own.</summary>
    public string PageSettingsToggleText => PageSettingsOverrideHeader + (UsePageSettingsOverride ? "  (on)" : "");

    /// <summary>How many pages of the job have settings of their own.</summary>
    public int PageSettingsOverrideCount => _pagePrintOverrides.Count;

    public bool UsePageSettingsOverride
    {
        get => PreviewOverride is not null;
        set
        {
            if (value == (PreviewOverride is not null)) return;
            if (value) _pagePrintOverrides[_printPreviewPageIndex] = PagePrintOverride.From(BuildPrintOptions());
            else _pagePrintOverrides.Remove(_printPreviewPageIndex);
            RaisePageSettingsOverrideChanged();
        }
    }

    public PaperSizeInfo? OverridePaperSizeInfo
    {
        get => PreviewOverride is { } own
            ? PaperSizes.FirstOrDefault(p => string.Equals(p.Name, own.PaperSizeName, StringComparison.OrdinalIgnoreCase)) ?? SelectedPaperSizeInfo
            : SelectedPaperSizeInfo;
        set
        {
            if (PreviewOverride is not { } own || value is null) return;
            own.PaperSizeName = value.Name;
            RaisePageSettingsOverrideChanged();
        }
    }

    public bool OverrideIsLandscape
    {
        get => (PreviewOverride?.Orientation ?? (IsLandscape ? PageOrientation.Landscape : PageOrientation.Portrait)) == PageOrientation.Landscape;
        set
        {
            if (PreviewOverride is not { } own) return;
            own.Orientation = value ? PageOrientation.Landscape : PageOrientation.Portrait;
            RaisePageSettingsOverrideChanged();
        }
    }

    public bool OverrideFitToPage
    {
        get => PreviewOverride?.FitToPage ?? FitToPage;
        set
        {
            if (PreviewOverride is not { } own) return;
            own.FitToPage = value;
            RaisePageSettingsOverrideChanged();
        }
    }

    public double OverrideScalePercent
    {
        get => PreviewOverride?.ScalePercent ?? ScalePercent;
        set
        {
            if (PreviewOverride is not { } own) return;
            own.ScalePercent = Math.Clamp(value, 10, 500);
            RaisePageSettingsOverrideChanged();
        }
    }

    /// <summary>Clicking the previewed sheet flips Portrait/Landscape — for the page's own settings if it has any, otherwise the common ones.</summary>
    public void TogglePreviewOrientation()
    {
        if (PreviewOverride is { } own)
            own.Orientation = own.Orientation == PageOrientation.Landscape ? PageOrientation.Portrait : PageOrientation.Landscape;
        else
            IsLandscape = !IsLandscape;
        RaisePageSettingsOverrideChanged();
    }

    private void RaisePageSettingsOverrideChanged()
    {
        OnPropertyChanged(nameof(UsePageSettingsOverride));
        OnPropertyChanged(nameof(PageSettingsOverrideHeader));
        OnPropertyChanged(nameof(PageSettingsToggleText));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(PageSettingsOverrideCount));
        OnPropertyChanged(nameof(OverridePaperSizeInfo));
        OnPropertyChanged(nameof(OverrideIsLandscape));
        OnPropertyChanged(nameof(OverrideFitToPage));
        OnPropertyChanged(nameof(OverrideScalePercent));
        OnPropertyChanged(nameof(PreviewIsLandscape));
        OnPropertyChanged(nameof(PreviewFitToPage));
        OnPropertyChanged(nameof(PreviewScalePercent));
    }

    /// <summary>Re-renders the preview for the current preview page. Safe to call often (e.g. every
    /// time the dialog is re-activated, to pick up effect/polygon edits made in the main window in
    /// the meantime): a newer request supersedes an older one still in flight, so a slow earlier
    /// render can never overwrite a later one's result.</summary>
    public async Task RefreshPrintPreviewAsync()
    {
        if (_pageFilePaths.Count == 0 || _printPreviewPages.Count == 0)
        {
            ++_printPreviewRenderToken; // a render still in flight for a page that is no longer in the range must not land
            PrintPreviewImage = null;
            return;
        }

        int token = ++_printPreviewRenderToken;
        IsPrintPreviewRendering = true;
        try
        {
            var page = await BuildPrintPageAsync(Math.Clamp(_printPreviewPageIndex, 0, _pageFilePaths.Count - 1));
            if (token != _printPreviewRenderToken) return; // superseded by a newer request
            PrintPreviewImage = PageToBitmapSource(page);
        }
        catch (Exception ex)
        {
            // A job switch mid-render (pages replaced under us) lands here — harmless, the next
            // refresh will render the new job. Anything else is worth a log line.
            _logger.LogWarning(ex, "Print preview render failed.");
        }
        finally
        {
            if (token == _printPreviewRenderToken)
                IsPrintPreviewRendering = false;
        }
    }


    // ---- Split settings ----

    private int _splitRows = 1;
    public int SplitRows { get => _splitRows; set => SetField(ref _splitRows, Math.Max(1, value)); }

    private int _splitColumns = 1;
    public int SplitColumns { get => _splitColumns; set => SetField(ref _splitColumns, Math.Max(1, value)); }

    private int _overlapPixels = 20;
    public int OverlapPixels { get => _overlapPixels; set => SetField(ref _overlapPixels, Math.Max(0, value)); }

    // ---- Per-page override ----

    private bool _useOverrideForCurrentPage;
    public bool UseOverrideForCurrentPage
    {
        get => _useOverrideForCurrentPage;
        set
        {
            if (!SetField(ref _useOverrideForCurrentPage, value)) return;
            if (_suppressOverrideWrite) return;

            if (value)
                SaveCurrentPageOverride();
            else
                _pageOverrides.Remove(CurrentPageIndex);

            OnPropertyChanged(nameof(OverrideCount));
        }
    }

    private int _overrideRows = 1;
    public int OverrideRows
    {
        get => _overrideRows;
        set
        {
            if (!SetField(ref _overrideRows, Math.Max(1, value))) return;
            if (!_suppressOverrideWrite && UseOverrideForCurrentPage) SaveCurrentPageOverride();
        }
    }

    private int _overrideColumns = 1;
    public int OverrideColumns
    {
        get => _overrideColumns;
        set
        {
            if (!SetField(ref _overrideColumns, Math.Max(1, value))) return;
            if (!_suppressOverrideWrite && UseOverrideForCurrentPage) SaveCurrentPageOverride();
        }
    }

    private int _overrideOverlapPixels = 20;
    public int OverrideOverlapPixels
    {
        get => _overrideOverlapPixels;
        set
        {
            if (!SetField(ref _overrideOverlapPixels, Math.Max(0, value))) return;
            if (!_suppressOverrideWrite && UseOverrideForCurrentPage) SaveCurrentPageOverride();
        }
    }

    /// <summary>How many pages in the current job have a custom override, for the status/summary text.</summary>
    public int OverrideCount => _pageOverrides.Count;

    private void SaveCurrentPageOverride()
    {
        _pageOverrides[CurrentPageIndex] = new SplitSettings
        {
            Rows = OverrideRows,
            Columns = OverrideColumns,
            OverlapPixels = OverrideOverlapPixels,
        };
        OnPropertyChanged(nameof(OverrideCount));
    }

    /// <summary>The settings that actually apply to a given document page: its own override if
    /// one was saved, otherwise the global defaults.</summary>
    private SplitSettings GetEffectiveSettings(int docPageIndex) =>
        _pageOverrides.TryGetValue(docPageIndex, out var pageOverride)
            ? pageOverride
            : new SplitSettings { Rows = SplitRows, Columns = SplitColumns, OverlapPixels = OverlapPixels };

    /// <summary>Preview of every split page, in print order, each labeled with its position.</summary>
    public ObservableCollectionSlim<TilePreviewItem> SplitPreviewTiles { get; } = new();

    public ObservableCollectionSlim<string> PrinterNames { get; } = new();

    private string? _selectedPrinter;
    public string? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetField(ref _selectedPrinter, value))
            {
                RefreshPaperSizes();
                PrintAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ---- Print settings ----

    public ObservableCollectionSlim<PaperSizeInfo> PaperSizes { get; } = new();

    private PaperSizeInfo? _selectedPaperSizeInfo;
    public PaperSizeInfo? SelectedPaperSizeInfo
    {
        get => _selectedPaperSizeInfo;
        set
        {
            if (SetField(ref _selectedPaperSizeInfo, value))
                RefreshSplitPreviewMockups();
        }
    }

    /// <summary>Just the name, for building PrintOptions — the printing service matches by name.</summary>
    public string? SelectedPaperSize => SelectedPaperSizeInfo?.Name;

    private bool _isLandscape;
    public bool IsLandscape
    {
        get => _isLandscape;
        set
        {
            if (SetField(ref _isLandscape, value))
            {
                RefreshSplitPreviewMockups();
                RaisePageSettingsOverrideChanged();
            }
        }
    }

    private double _scalePercent = 100;
    public double ScalePercent
    {
        get => _scalePercent;
        set
        {
            if (SetField(ref _scalePercent, Math.Clamp(value, 10, 500)))
            {
                RefreshSplitPreviewMockups();
                RaisePageSettingsOverrideChanged();
            }
        }
    }

    private bool _fitToPage = true;
    public bool FitToPage
    {
        get => _fitToPage;
        set
        {
            if (SetField(ref _fitToPage, value))
            {
                RefreshSplitPreviewMockups();
                RaisePageSettingsOverrideChanged();
            }
        }
    }

    private double _marginLeftInches = 0.25;
    public double MarginLeftInches
    {
        get => _marginLeftInches;
        set { if (SetField(ref _marginLeftInches, Math.Max(0, value))) RefreshSplitPreviewMockups(); }
    }

    private double _marginTopInches = 0.25;
    public double MarginTopInches
    {
        get => _marginTopInches;
        set { if (SetField(ref _marginTopInches, Math.Max(0, value))) RefreshSplitPreviewMockups(); }
    }

    private double _marginRightInches = 0.25;
    public double MarginRightInches
    {
        get => _marginRightInches;
        set { if (SetField(ref _marginRightInches, Math.Max(0, value))) RefreshSplitPreviewMockups(); }
    }

    private double _marginBottomInches = 0.25;
    public double MarginBottomInches
    {
        get => _marginBottomInches;
        set { if (SetField(ref _marginBottomInches, Math.Max(0, value))) RefreshSplitPreviewMockups(); }
    }

    private bool _showPageNumbers;
    public bool ShowPageNumbers { get => _showPageNumbers; set => SetField(ref _showPageNumbers, value); }

    /// <summary>Display-friendly names for the ComboBox; parsed back to the enum when building PrintOptions.</summary>
    public ObservableCollectionSlim<string> PageNumberPositions { get; } = new(new[]
    {
        "Header Left", "Header Center", "Header Right", "Footer Left", "Footer Center", "Footer Right",
    });

    private string _selectedPageNumberPosition = "Footer Center";
    public string SelectedPageNumberPosition { get => _selectedPageNumberPosition; set => SetField(ref _selectedPageNumberPosition, value); }

    private int _copies = 1;
    public int Copies { get => _copies; set => SetField(ref _copies, Math.Clamp(value, 1, 999)); }

    /// <summary>True prints each page whole (whitened/cropped, no split) — false (the default,
    /// preserving prior behavior) prints whatever's currently in _currentSplitTiles, which requires
    /// Apply Split/Process All Pages to have been run first. See PrintAllCommand's CanExecute and
    /// PrintAllAsync for how this changes what "Print All Pages" actually does.</summary>
    private bool _printWholePage;
    public bool PrintWholePage
    {
        get => _printWholePage;
        set
        {
            if (SetField(ref _printWholePage, value))
                PrintAllCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Which pages to include in Size-mode (whole-page) print/export — see PageRangeMode
    /// and ResolvePageIndicesToPrint. Doesn't affect Split mode, which already has its own distinct
    /// workflow for "just this page" vs "every page".</summary>
    private PageRangeMode _pageRangeMode = PageRangeMode.All;
    public PageRangeMode PageRangeMode
    {
        get => _pageRangeMode;
        set
        {
            if (!SetField(ref _pageRangeMode, value)) return;
            // "Current Page" is the page being previewed at the moment it is chosen.
            if (value == PageRangeMode.Current) _printCurrentPageIndex = _printPreviewPageIndex;
            RebuildPrintPreviewPages(refreshImage: true);
        }
    }

    /// <summary>A page-range string like "1-3,5,8-10", parsed by ResolvePageIndicesToPrint —
    /// only consulted when PageRangeMode is Selected.</summary>
    private string _selectedPagesRangeText = "";
    public string SelectedPagesRangeText
    {
        get => _selectedPagesRangeText;
        set
        {
            if (!SetField(ref _selectedPagesRangeText, value)) return;
            RebuildPrintPreviewPages(refreshImage: false);
            SchedulePrintPreviewRefresh();
        }
    }

    /// <summary>Resolves PageRangeMode into the actual 0-based indices (into _pageFilePaths) to
    /// print/export in whole-page mode — All is every page, Current is just CurrentPageIndex,
    /// Selected parses SelectedPagesRangeText. Out-of-range or unparseable entries in a Selected
    /// range are silently skipped rather than raising an error, since a page range is exactly the
    /// kind of freeform text field where a person will often leave a stray typo in place.</summary>
    private List<int> ResolvePageIndicesToPrint() =>
        PrintPageRange.Resolve(PrintRange, _printCurrentPageIndex, SelectedPagesRangeText, _pageFilePaths.Count);

    private void RefreshPaperSizes()
    {
        PaperSizes.Clear();
        if (SelectedPrinter is null) return;

        foreach (var size in _printingService.GetAvailablePaperSizes(SelectedPrinter))
            PaperSizes.Add(size);

        // Prefer A4 if the printer offers it, otherwise just take whatever's first.
        SelectedPaperSizeInfo = PaperSizes.FirstOrDefault(s => s.Name.Equals("A4", StringComparison.OrdinalIgnoreCase))
            ?? PaperSizes.FirstOrDefault();
    }

    private PrintOptions BuildPrintOptions() => new()
    {
        PaperSizeName = SelectedPaperSize,
        Orientation = IsLandscape ? PageOrientation.Landscape : PageOrientation.Portrait,
        ScalePercent = ScalePercent,
        FitToPage = FitToPage,
        MarginLeftInches = MarginLeftInches,
        MarginTopInches = MarginTopInches,
        MarginRightInches = MarginRightInches,
        MarginBottomInches = MarginBottomInches,
        ShowPageNumbers = ShowPageNumbers,
        PageNumberPosition = ParsePageNumberPosition(SelectedPageNumberPosition),
        Copies = Copies,
    };

    private static PageNumberPosition ParsePageNumberPosition(string label) => label switch
    {
        "Header Left" => PageNumberPosition.HeaderLeft,
        "Header Center" => PageNumberPosition.HeaderCenter,
        "Header Right" => PageNumberPosition.HeaderRight,
        "Footer Left" => PageNumberPosition.FooterLeft,
        "Footer Right" => PageNumberPosition.FooterRight,
        _ => PageNumberPosition.FooterCenter,
    };

    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand ToggleBeforeAfterCommand { get; }
    public RelayCommand ApplySplitCommand { get; }
    public RelayCommand ApplySplitAllPagesCommand { get; }
    public RelayCommand PrintAllCommand { get; }

    // ---- Split / print ----

    private void ApplySplitCurrentPage()
    {
        if (_currentWhitenedPage is null) return;

        var settings = GetEffectiveSettings(CurrentPageIndex);
        // Hidden rows/columns come out BEFORE splitting, so tiles are cut from what's actually shown.
        var tiles = _pageSplitting.Split(RemoveHiddenAreasFromPage(_currentWhitenedPage, CurrentPageIndex + 1), settings);
        _currentSplitTiles = tiles;
        _currentSplitTileLabels = tiles
            .Select(t => $"Page {t.RowIndex * settings.Columns + t.ColumnIndex + 1} (row {t.RowIndex + 1}, col {t.ColumnIndex + 1})")
            .ToList();

        RefreshSplitPreviewMockups();

        StatusText = $"Split current page into {tiles.Count} tile(s) ({settings.Rows}x{settings.Columns}, " +
                     $"{settings.OverlapPixels}px overlap{(UseOverrideForCurrentPage ? ", custom" : "")}). Ready to print.";
        RaiseAllCanExecuteChanged();
    }

    /// <summary>
    /// Whitens and splits EVERY page in the job (not just the currently displayed one) and
    /// combines all resulting tiles, in document-page order, into one printable set. This is what
    /// lets "Print All Pages" send a whole multi-page job to the printer in a single command
    /// instead of requiring you to split-and-print each page one at a time.
    /// </summary>
    private async Task ApplySplitAllPagesAsync()
    {
        if (_pageFilePaths.Count == 0) return;

        IsBusy = true;
        try
        {
            var allTiles = new List<SplitTile>();
            var allLabels = new List<string>();

            for (int docPageIndex = 0; docPageIndex < _pageFilePaths.Count; docPageIndex++)
            {
                var settings = GetEffectiveSettings(docPageIndex);
                bool isCustom = _pageOverrides.ContainsKey(docPageIndex);

                StatusText = $"Processing page {docPageIndex + 1} of {_pageFilePaths.Count}" +
                             $"{(isCustom ? " (custom settings)" : "")}...";

                var raw = await Task.Run(() => PngPageLoader.Load(_pageFilePaths[docPageIndex], docPageIndex + 1, _jobId, _capturedDpiOverride));
                var whitened = await _imageProcessing.ApplyEffectsAsync(raw, EffectSettings);
                if (EffectSettings.AutoCropEnabled)
                    whitened = await _imageProcessing.CropToContentAsync(whitened, EffectSettings.AutoCropWhiteThreshold, EffectSettings.AutoCropPaddingPixels);
                whitened = RemoveHiddenAreasFromPage(whitened, docPageIndex + 1); // same as the single-page split above
                var tiles = _pageSplitting.Split(whitened, settings);

                foreach (var tile in tiles)
                {
                    allTiles.Add(tile);
                    int tileNumber = tile.RowIndex * settings.Columns + tile.ColumnIndex + 1;
                    allLabels.Add($"Doc page {docPageIndex + 1}{(isCustom ? " *" : "")}, tile {tileNumber} " +
                                   $"(row {tile.RowIndex + 1}, col {tile.ColumnIndex + 1})");
                }
            }

            _currentSplitTiles = allTiles;
            _currentSplitTileLabels = allLabels;
            RefreshSplitPreviewMockups();

            StatusText = $"Processed all {_pageFilePaths.Count} page(s) -> {allTiles.Count} total tile(s). " +
                         $"{OverrideCount} page(s) used custom settings (marked *). Ready to print.";
        }
        finally
        {
            IsBusy = false; // also raises RaiseAllCanExecuteChanged via the IsBusy setter
        }
    }

    /// <summary>
    /// Prints either every page whole (PrintWholePage=true — whitened/cropped, no split step
    /// required beforehand at all) or whatever's currently in _currentSplitTiles (the existing
    /// behavior, requiring Apply Split/Process All Pages to have been run first). Both paths share
    /// the same underlying IPrintingService.PrintTilesAsync — a "whole page" is represented as a
    /// single 1x1 SplitTile, so no printing-service changes were needed for this.
    /// </summary>
    private async Task PrintAllAsync()
    {
        if (SelectedPrinter is null) return;
        if (!PrintWholePage && _currentSplitTiles.Count == 0) return;
        if (PrintWholePage && _pageFilePaths.Count == 0) return;

        IsBusy = true;
        try
        {
            IReadOnlyList<SplitTile> tilesToPrint;

            if (PrintWholePage)
            {
                var pageIndices = ResolvePageIndicesToPrint();
                if (pageIndices.Count == 0)
                {
                    StatusText = "No pages match the selected page range.";
                    return;
                }

                var wholePageTiles = new List<SplitTile>();
                for (int i = 0; i < pageIndices.Count; i++)
                {
                    int docPageIndex = pageIndices[i];
                    StatusText = $"Preparing page {docPageIndex + 1} ({i + 1} of {pageIndices.Count}) for printing...";
                    var whitened = await BuildPrintPageAsync(docPageIndex);

                    wholePageTiles.Add(new SplitTile
                    {
                        RowIndex = 0,
                        ColumnIndex = 0,
                        Width = whitened.Width,
                        Height = whitened.Height,
                        PixelData = whitened.PixelData,
                        Dpi = whitened.Dpi,
                    });
                }
                tilesToPrint = wholePageTiles;
            }
            else
            {
                tilesToPrint = _currentSplitTiles;
            }

            // Whole pages: a page that was set apart in the Print dialog goes with its own paper size, orientation and scale (still one job). Split tiles have no per-page settings of this kind.
            IReadOnlyList<PagePrintOverride?>? pageOverrides = PrintWholePage
                ? ResolvePageIndicesToPrint().Select(i => _pagePrintOverrides.TryGetValue(i, out var own) ? own.Clone() : null).ToList()
                : null;

            StatusText = $"Printing {tilesToPrint.Count} page(s) to {SelectedPrinter} ({Copies} cop{(Copies == 1 ? "y" : "ies")})...";
            await _printingService.PrintTilesAsync(tilesToPrint, SelectedPrinter, BuildPrintOptions(), default, pageOverrides);
            StatusText = $"Sent {tilesToPrint.Count} page(s) to {SelectedPrinter}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing failed.");
            StatusText = $"Print failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Draws every polygon marked IncludeInPrint (for the given page number + current Before/After
    /// variant) directly onto the page's pixel data — fill first, then outline on top, matching the
    /// same visual layering the live preview uses. Must run AFTER auto-crop, not before (an earlier
    /// version of this got this backwards) — polygon vertices are stored relative to whatever
    /// PreviewImage was actually showing at the time they were drawn, and LoadPageAsync applies
    /// auto-crop BEFORE ever setting PreviewImage, so with auto-crop on (the default) a polygon's
    /// coordinates are already relative to the CROPPED page, not the original full one. Drawing
    /// before crop meant the polygon landed offset by however much whitespace crop had removed —
    /// most visible vertically, since a scanned page's top margin is often a different size than
    /// its side margins, so the mismatch was more noticeable in Y than X. Drawing after crop
    /// instead means both are already in the same coordinate space with nothing to compensate for.
    ///
    /// OpenCvSharp types are fully qualified throughout rather than "using OpenCvSharp;" at the top
    /// of the file, since OpenCvSharp.Point would otherwise collide with System.Windows.Point,
    /// which this file already uses extensively (MapDisplayToOriginal, the rectangle-drawing
    /// corner tracking, etc.).
    ///
    /// Scoped to whole-page print/export only — SplitTile doesn't currently track its own offset
    /// within the original page, so correctly translating and clipping a polygon onto an
    /// individual split tile isn't implemented yet.
    /// </summary>
    private CapturedPage DrawIncludedPolygonsOntoPage(CapturedPage page, int pageNumber)
    {
        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;
        if (!_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants) ||
            !variants.TryGetValue(variant, out var pageAnnotations))
            return page;

        // For a Hide Row/Column, IncludeInPrint means "apply the hide" (see RemoveHiddenAreasFromPage) — its
        // outline is an editing aid and is never drawn onto the page. A text label needs just its anchor point
        // and some text; every other shape needs at least two points.
        var toDraw = pageAnnotations.Polygons
            .Where(p => p.IncludeInPrint
                        && p.Purpose is not (PolygonPurpose.HideRow or PolygonPurpose.HideColumn)
                        && (p.Kind == ShapeKind.Text ? p.Vertices.Count >= 1 && !string.IsNullOrEmpty(p.Text)
                            : p.Kind == ShapeKind.Image ? p.Vertices.Count >= 1 && !string.IsNullOrEmpty(p.ImageFile)
                            : p.Vertices.Count >= 2))
            .ToList();
        if (toDraw.Count == 0) return page;

        // PolygonPoint's own vertices are stored in WPF DIU (96-DPI-based device-independent
        // units, matching PreviewImage.Width/Height) — NOT raw pixels. CapturedPage.PixelData is
        // raw pixels at the page's actual DPI (e.g. 600). Without this conversion, vertex
        // coordinates land squished toward the origin at a small fraction of their intended
        // position/size (e.g. a point meant to be near the middle of the page ends up in a tiny
        // patch near the top-left corner) — which, combined with CropToContentAsync running AFTER
        // this method and expanding its crop box to include whatever it just drew, is exactly what
        // caused the polygon to show up tiny, in the wrong place, with extra whitespace added
        // above the real content to accommodate it.
        double pixelsPerDiu = page.Dpi / 96.0;

        using var mat = new OpenCvSharp.Mat(page.Height, page.Width, OpenCvSharp.MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(page.PixelData, 0, mat.Data, page.PixelData.Length);

        var textsToComposite = new List<PolygonAnnotation>();

        foreach (var polygon in toDraw)
        {
            // A picture is composited right here, in stacking order with the shapes (a shape drawn after it goes on top of it, as in the preview).
            if (polygon.Kind == ShapeKind.Image)
            {
                CompositeImageOntoPage(mat, polygon, pixelsPerDiu, page.Width, page.Height);
                continue;
            }

            // Text is composited as a separate step after the OpenCV drawing (OpenCV has no usable fonts).
            if (polygon.Kind == ShapeKind.Text)
            {
                textsToComposite.Add(polygon);
                continue;
            }

            // The outline: the vertices themselves for a polygon/rectangle, or — for a circle, ellipse, arc
            // or curve — the true curve flattened here in PAGE PIXELS to a fifth of a pixel. Curves are drawn
            // with 3 fractional bits of coordinate precision (OpenCV's `shift`), so the points keep their
            // sub-pixel position and the anti-aliased line is smooth rather than faintly wobbling from
            // rounding every point to a whole pixel. Plain polygons keep shift 0, exactly as before.
            int shift = polygon.Kind == ShapeKind.Polygon ? 0 : 3;
            double scale = 1 << shift;
            var control = polygon.Vertices.Select(v => new GeoPoint(v.X * pixelsPerDiu, v.Y * pixelsPerDiu)).ToList();
            var outline = polygon.Kind == ShapeKind.Polygon
                ? control
                : ShapeGeometry.Flatten(polygon.Kind, control, polygon.IsClosed, 0.2);
            var points = outline
                .Select(g => new OpenCvSharp.Point((int)Math.Round(g.X * scale), (int)Math.Round(g.Y * scale)))
                .ToArray();

            if (polygon.IsClosed && !string.IsNullOrEmpty(polygon.FillColorHex))
            {
                var fillColor = HexToColor(polygon.FillColorHex);
                double alpha = Math.Clamp(polygon.FillOpacity / 100.0, 0, 1);
                using var overlay = mat.Clone();
                OpenCvSharp.Cv2.FillPoly(overlay, new[] { points }, new OpenCvSharp.Scalar(fillColor.B, fillColor.G, fillColor.R, 255),
                    lineType: OpenCvSharp.LineTypes.AntiAlias, shift: shift);
                OpenCvSharp.Cv2.AddWeighted(overlay, alpha, mat, 1 - alpha, 0, mat);
            }

            if (polygon.HasStroke)
            {
                var strokeColor = HexToColor(polygon.StrokeColorHex);
                // Scaling thickness by the FULL pixelsPerDiu factor (true physical size — the same
                // "3 DIU" line prints at the same physical width regardless of DPI) looked much
                // thicker than expected: the app's own preview is normally viewed well below 100%
                // zoom for a large scanned page, so the on-screen impression of "how thick this
                // line looks" is already smaller than its true physical size would suggest — full
                // physical scaling then looks disproportionately heavy once printed at actual size.
                // Damping with a square root gives a middle ground between that (too thick) and no
                // scaling at all (a hairline, easy to lose at high DPI) — tune further if still off.
                int thickness = Math.Max(1, (int)Math.Round(polygon.StrokeThickness * Math.Sqrt(pixelsPerDiu)));
                // LineTypes.AntiAlias (anti-aliased) rather than OpenCV's default 8-connected line type —
                // the default draws diagonal/curved-looking segments as hard, stair-stepped pixels,
                // which is clearly visible once printed at 300+ DPI; AA blends the edge pixels
                // instead. Axis-aligned segments (e.g. rectangles) are unaffected either way.
                OpenCvSharp.Cv2.Polylines(mat, new[] { points }, polygon.IsClosed,
                    new OpenCvSharp.Scalar(strokeColor.B, strokeColor.G, strokeColor.R, 255), thickness,
                    lineType: OpenCvSharp.LineTypes.AntiAlias, shift: shift);
            }
        }

        // Behind each text that asks for it: blur the page (and any shapes just drawn onto it). Done on the pixels
        // before they're copied out, and before the text goes on top.
        foreach (var text in textsToComposite.Where(t => t.BlurBackground))
            ApplyBackdropBlur(mat, text, pixelsPerDiu);

        var outBytes = new byte[mat.Total() * mat.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, outBytes, 0, outBytes.Length);

        foreach (var text in textsToComposite)
        {
            var anchor = text.Vertices[0];

            // The frame goes on first, so the letters are drawn over it (as on screen).
            if (text.FrameVisible)
            {
                Color? frameLine = text.FrameThickness > 0 ? HexToColor(text.FrameStrokeColorHex) : null;
                Color? frameFill = string.IsNullOrEmpty(text.FrameFillColorHex) ? null : HexToColor(text.FrameFillColorHex);
                var frame = TextRenderer.RasterizeFrameForPrint(StyleOf(text), frameLine, text.FrameThickness, text.FrameLineStyle, frameFill, text.FrameOpacity / 100.0,
                    TextGeometry.PointsToDiu(text.FramePaddingPoints), TextGeometry.PointsToDiu(text.FrameCornerRadiusPoints), text.RotationDegrees, pixelsPerDiu);
                if (frame is { } frameBitmap)
                    CompositeBgra(outBytes, page.Width, page.Height, frameBitmap.Pixels, frameBitmap.Width, frameBitmap.Height,
                        (int)Math.Round(anchor.X * pixelsPerDiu) + frameBitmap.OffsetX, (int)Math.Round(anchor.Y * pixelsPerDiu) + frameBitmap.OffsetY);
            }

            Color? letters = string.IsNullOrEmpty(text.FillColorHex) ? null : HexToColor(text.FillColorHex);
            Color? outline = text.HasStroke ? HexToColor(text.StrokeColorHex) : null;
            var rendered = TextRenderer.RasterizeForPrint(StyleOf(text), letters, outline, TextOutlineWidthDiu(text.StrokeThickness),
                text.FillOpacity / 100.0, text.RotationDegrees, pixelsPerDiu);
            if (rendered is { } bitmap)
                CompositeBgra(outBytes, page.Width, page.Height, bitmap.Pixels, bitmap.Width, bitmap.Height,
                    (int)Math.Round(anchor.X * pixelsPerDiu) + bitmap.OffsetX, (int)Math.Round(anchor.Y * pixelsPerDiu) + bitmap.OffsetY);
        }

        return new CapturedPage
        {
            PageNumber = page.PageNumber,
            Width = page.Width,
            Height = page.Height,
            Dpi = page.Dpi,
            PixelData = outBytes,
            SourceJobId = page.SourceJobId,
        };
    }

    /// <summary>
    /// The printed counterpart of the overlay's blurred halo: Gaussian-blurs the page in a band round the LETTERS (not their bounding
    /// box), fading the effect out towards the edge by the text's softness. The letters are rendered the way they will be printed
    /// (outline included, rotation applied) to find which pixels are ink; a distance transform then gives every other pixel its
    /// distance to the nearest ink, and <see cref="TextGeometry.HaloStrength"/> turns that into how much of the blur to show — the
    /// same profile the preview draws. The blur is computed over a region a little larger than the one it affects, so the blur at
    /// the halo's edge uses the real surrounding pixels instead of reflecting its own border.
    /// </summary>
    private static void ApplyBackdropBlur(OpenCvSharp.Mat mat, PolygonAnnotation text, double pixelsPerDiu)
    {
        double area = TextGeometry.PointsToDiu(text.BlurAreaPoints) * pixelsPerDiu;
        if (area <= 0) return;
        double feather = TextGeometry.FeatherWidth(area, text.BlurSoftnessPercent);

        var style = StyleOf(text);
        var anchor = new GeoPoint(Math.Round(text.Vertices[0].X * pixelsPerDiu), Math.Round(text.Vertices[0].Y * pixelsPerDiu));
        Color? outline = text.HasStroke ? Colors.Black : null;
        var ink = TextRenderer.RasterizeForPrint(style, Colors.Black, outline, TextOutlineWidthDiu(text.StrokeThickness), 1.0, text.RotationDegrees, pixelsPerDiu);

        // Where the ink is, as a rectangle of page pixels (inkX, inkY, inkW, inkH).
        int inkX, inkY, inkW, inkH;
        if (ink is { } rendered)
        {
            inkX = (int)anchor.X + rendered.OffsetX; inkY = (int)anchor.Y + rendered.OffsetY;
            inkW = rendered.Width; inkH = rendered.Height;
        }
        else
        {
            var layout = TextRenderer.Measure(style);
            var box = TextGeometry.BoundsOf(TextGeometry.RectangleCorners(anchor, text.RotationDegrees, 0, 0, layout.Width * pixelsPerDiu, layout.Height * pixelsPerDiu));
            inkX = (int)Math.Floor(box.MinX); inkY = (int)Math.Floor(box.MinY);
            inkW = (int)Math.Ceiling(box.MaxX - box.MinX); inkH = (int)Math.Ceiling(box.MaxY - box.MinY);
        }

        double sigma = Math.Max(0.5, BackdropBlurRadiusDiu / 3.0 * pixelsPerDiu);
        int margin = (int)Math.Ceiling(area) + (int)Math.Ceiling(sigma * 3) + 2;
        int x0 = Math.Max(0, inkX - margin), y0 = Math.Max(0, inkY - margin);
        int x1 = Math.Min(mat.Width, inkX + inkW + margin), y1 = Math.Min(mat.Height, inkY + inkH + margin);
        if (x1 - x0 < 2 || y1 - y0 < 2) return;

        var region = new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0);
        int pixelCount = region.Width * region.Height;

        // 0 where there is ink, 255 everywhere else: the distance transform measures distance to the nearest zero.
        var inkMap = new byte[pixelCount];
        Array.Fill(inkMap, (byte)255);
        if (ink is { } bitmap)
        {
            for (int sy = 0; sy < bitmap.Height; sy++)
            {
                int py = inkY + sy - y0;
                if (py < 0 || py >= region.Height) continue;
                for (int sx = 0; sx < bitmap.Width; sx++)
                {
                    int px = inkX + sx - x0;
                    if (px < 0 || px >= region.Width) continue;
                    if (bitmap.Pixels[(sy * bitmap.Width + sx) * 4 + 3] > 8) inkMap[py * region.Width + px] = 0;
                }
            }
        }
        else
        {
            Array.Clear(inkMap); // no letters to measure from (empty text): treat the whole box as ink
        }

        using var inkMat = new OpenCvSharp.Mat(region.Height, region.Width, OpenCvSharp.MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(inkMap, 0, inkMat.Data, pixelCount);
        using var distanceMat = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.DistanceTransform(inkMat, distanceMat, OpenCvSharp.DistanceTypes.L2, OpenCvSharp.DistanceTransformMasks.Precise, OpenCvSharp.MatType.CV_32F);
        var distances = new float[pixelCount];
        System.Runtime.InteropServices.Marshal.Copy(distanceMat.Data, distances, 0, pixelCount);

        var maskBytes = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
            maskBytes[i] = (byte)Math.Round(TextGeometry.HaloStrength(distances[i], area, feather) * 255);

        using var view = new OpenCvSharp.Mat(mat, region);          // a window onto the page itself
        using var original = view.Clone();
        using var blurred = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.GaussianBlur(original, blurred, new OpenCvSharp.Size(0, 0), sigma);

        int channels = original.Channels();
        var originalBytes = new byte[pixelCount * channels];
        var blurredBytes = new byte[originalBytes.Length];
        System.Runtime.InteropServices.Marshal.Copy(original.Data, originalBytes, 0, originalBytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(blurred.Data, blurredBytes, 0, blurredBytes.Length);

        // result = original x (1 - m) + blurred x m, per channel, with m the halo strength as 0..1
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int m = maskBytes[pixel];
            if (m == 0) continue;
            for (int c = 0; c < channels; c++)
            {
                int i = pixel * channels + c;
                originalBytes[i] = (byte)((originalBytes[i] * (255 - m) + blurredBytes[i] * m + 127) / 255);
            }
        }

        using var result = new OpenCvSharp.Mat(region.Height, region.Width, original.Type());
        System.Runtime.InteropServices.Marshal.Copy(originalBytes, 0, result.Data, originalBytes.Length);
        result.CopyTo(view);                                        // writes straight back into the page
    }

    /// <summary>Draws a picture annotation onto the page's pixels: the file at full resolution, scaled to the page, flipped, rotated about its anchor and faded to
    /// its opacity (ImageRenderer), blended over what is already there. A picture whose file has gone is simply not drawn.</summary>
    private void CompositeImageOntoPage(OpenCvSharp.Mat mat, PolygonAnnotation image, double pixelsPerDiu, int pageWidth, int pageHeight)
    {
        if (string.IsNullOrEmpty(image.ImageFile)) return;
        var bitmap = JobImageStore.Load(CurrentJobFolder, image.ImageFile); // full size: the printout deserves every pixel
        if (bitmap is null) return;

        var rendered = ImageRenderer.RasterizeForPrint(bitmap, image.ImageWidth, image.ImageHeight, image.FlipHorizontal, image.FlipVertical,
            image.FillOpacity / 100.0, image.RotationDegrees, pixelsPerDiu);
        if (rendered is not { } r) return;

        var anchor = image.Vertices[0];
        var pixels = new byte[mat.Total() * mat.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        CompositeBgra(pixels, pageWidth, pageHeight, r.Pixels, r.Width, r.Height,
            (int)Math.Round(anchor.X * pixelsPerDiu) + r.OffsetX, (int)Math.Round(anchor.Y * pixelsPerDiu) + r.OffsetY);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
    }

    /// <summary>Alpha-composites a premultiplied-BGRA source over an opaque BGRA destination at (x0, y0),
    /// clipped to the destination: out = src + dst * (1 - srcAlpha), per channel. The page stays opaque.</summary>
    private static void CompositeBgra(byte[] dst, int dstWidth, int dstHeight, byte[] src, int srcWidth, int srcHeight, int x0, int y0)
    {
        for (int sy = 0; sy < srcHeight; sy++)
        {
            int dy = y0 + sy;
            if (dy < 0 || dy >= dstHeight) continue;

            for (int sx = 0; sx < srcWidth; sx++)
            {
                int dx = x0 + sx;
                if (dx < 0 || dx >= dstWidth) continue;

                int si = (sy * srcWidth + sx) * 4;
                int alpha = src[si + 3];
                if (alpha == 0) continue;

                int di = (dy * dstWidth + dx) * 4;
                int inverse = 255 - alpha;
                dst[di] = (byte)Math.Min(255, src[si] + dst[di] * inverse / 255);
                dst[di + 1] = (byte)Math.Min(255, src[si + 1] + dst[di + 1] * inverse / 255);
                dst[di + 2] = (byte)Math.Min(255, src[si + 2] + dst[di + 2] * inverse / 255);
                dst[di + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Removes the page's Hide Row / Hide Column bands from its pixel data — the print/export/split
    /// counterpart of what RefreshPreviewImage does for the on-screen preview. Without this, hidden
    /// areas disappeared from the preview but were still printed, since the print pipeline never
    /// applied them at all. The hidden ranges are derived from the SAME saved annotations (same page
    /// number + Before/After variant) that DrawIncludedPolygonsOntoPage reads, using the same rule
    /// RecomputeHiddenBands uses for the live preview (every closed HideRow/HideColumn polygon's
    /// bounding box, merged), then removed with the same RemoveHiddenBands the preview uses — so what
    /// prints is exactly what the preview shows. Must run AFTER polygons are drawn (their vertices
    /// are in the page's original, pre-removal coordinate space, and a polygon sitting inside a
    /// hidden band should disappear along with it, exactly like in the preview).
    ///
    /// Only hides whose "Hide in Print" (IncludeInPrint) is ticked are applied — see the check below.
    /// </summary>
    private CapturedPage RemoveHiddenAreasFromPage(CapturedPage page, int pageNumber)
    {
        var variant = ShowBefore ? ImageVariant.Raw : ImageVariant.Cleaned;
        if (!_jobAnnotationSettings.Pages.TryGetValue(pageNumber, out var variants) ||
            !variants.TryGetValue(variant, out var pageAnnotations))
            return page;

        var yRanges = new List<(double Start, double End)>();
        var xRanges = new List<(double Start, double End)>();
        foreach (var polygon in pageAnnotations.Polygons)
        {
            if (!polygon.IsClosed || polygon.Vertices.Count < 3) continue;

            // A hide only reaches the output if it was explicitly included ("Hide in Print"). The on-screen
            // preview always hides it; the printed/exported page keeps it unless asked otherwise. (It used to be
            // always removed from the output, with no way to keep it.)
            if (!polygon.IncludeInPrint) continue;

            if (polygon.Purpose == PolygonPurpose.HideRow)
                yRanges.Add((polygon.Vertices.Min(v => v.Y), polygon.Vertices.Max(v => v.Y)));
            else if (polygon.Purpose == PolygonPurpose.HideColumn)
                xRanges.Add((polygon.Vertices.Min(v => v.X), polygon.Vertices.Max(v => v.X)));
        }
        if (yRanges.Count == 0 && xRanges.Count == 0) return page;

        var bitmap = PageToBitmapSource(page);
        bitmap = RemoveHiddenBands(bitmap, MergeRanges(yRanges), isRowWise: true);
        bitmap = RemoveHiddenBands(bitmap, MergeRanges(xRanges), isRowWise: false);

        int stride = bitmap.PixelWidth * 4; // Bgra32 throughout — see PageToBitmapSource
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        return new CapturedPage
        {
            PageNumber = page.PageNumber,
            Width = bitmap.PixelWidth,
            Height = bitmap.PixelHeight,
            Dpi = page.Dpi,
            PixelData = pixels,
            SourceJobId = page.SourceJobId,
        };
    }

    /// <summary>
    /// The ONE definition of "this page, exactly as it will be printed or exported whole": load,
    /// effects, auto-crop, "Include in Print" polygons drawn on, then hidden rows/columns removed.
    /// Print, whole-page export, and the Print dialog's own preview all go through this, so they
    /// can't drift apart again (they previously each repeated the load/effects/crop/polygons steps
    /// by hand, which is how hidden areas ended up handled by the preview but not by printing).
    ///
    /// Everything read from shared state (page path, job id, DPI override) is captured on the
    /// calling (UI) thread BEFORE the first await — a LoadJob can replace _pageFilePaths while this
    /// is mid-flight, and the pool thread inside Task.Run must not be reading it.
    /// </summary>
    private async Task<CapturedPage> BuildPrintPageAsync(int docPageIndex)
    {
        string path = _pageFilePaths[docPageIndex];
        string jobId = _jobId;
        int? dpiOverride = _capturedDpiOverride;
        int pageNumber = docPageIndex + 1;

        var raw = await Task.Run(() => PngPageLoader.Load(path, pageNumber, jobId, dpiOverride));
        var whitened = await _imageProcessing.ApplyEffectsAsync(raw, EffectSettings);
        if (EffectSettings.AutoCropEnabled)
            whitened = await _imageProcessing.CropToContentAsync(whitened, EffectSettings.AutoCropWhiteThreshold, EffectSettings.AutoCropPaddingPixels);

        whitened = DrawIncludedPolygonsOntoPage(whitened, pageNumber);
        return RemoveHiddenAreasFromPage(whitened, pageNumber);
    }

    /// <summary>
    /// Exports JUST the currently-displayed page (the "cleaned" — effects/crop applied — version,
    /// with any "Include in Print" polygons baked in, same as ExportAsync's whole-page mode does)
    /// as a single PNG/JPG file, at its true actual pixel size. Distinct from ExportAsync (which
    /// exports the whole job or the current split tiles depending on PrintWholePage) — this is
    /// specifically "just this one page, right now", for MainWindow's own Export button and its X
    /// shortcut.
    /// </summary>
    public async Task ExportCurrentPageAsync(string destinationPath, string format)
    {
        if (_currentWhitenedPage is null)
            throw new InvalidOperationException("Nothing to export — no page is loaded.");

        var page = DrawIncludedPolygonsOntoPage(_currentWhitenedPage, CurrentPageIndex + 1);
        page = RemoveHiddenAreasFromPage(page, CurrentPageIndex + 1);
        var image = new ExportImage(page.Width, page.Height, page.Dpi, page.PixelData, "page");

        IsBusy = true;
        try
        {
            StatusText = $"Exporting to {destinationPath}...";
            await Task.Run(() => ExportAsImageFiles(new List<ExportImage> { image }, destinationPath, format));
            StatusText = $"Exported to {destinationPath}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Exports either every page whole (PrintWholePage=true — reusing the same toggle Print All
    /// Pages uses) or the current split tiles, as PNG/JPG — one file per page/tile, at the image's
    /// own actual pixel size, never scaled/fitted to any paper size.
    /// </summary>
    public async Task ExportAsync(string destinationPath, string format)
    {
        var images = new List<ExportImage>();

        IsBusy = true;
        try
        {
            if (PrintWholePage)
            {
                var pageIndices = ResolvePageIndicesToPrint();
                for (int i = 0; i < pageIndices.Count; i++)
                {
                    int docPageIndex = pageIndices[i];
                    StatusText = $"Preparing page {docPageIndex + 1} ({i + 1} of {pageIndices.Count}) for export...";
                    var whitened = await BuildPrintPageAsync(docPageIndex);
                    images.Add(new ExportImage(whitened.Width, whitened.Height, whitened.Dpi, whitened.PixelData, $"page{docPageIndex + 1}"));
                }
            }
            else
            {
                if (_currentSplitTiles.Count == 0)
                    throw new InvalidOperationException("No split tiles to export. Run Apply Split (This Page) or Process All Pages first, or switch to Print Whole Page mode.");

                for (int i = 0; i < _currentSplitTiles.Count; i++)
                {
                    var tile = _currentSplitTiles[i];
                    images.Add(new ExportImage(tile.Width, tile.Height, tile.Dpi, tile.PixelData, $"tile{i + 1}_r{tile.RowIndex + 1}c{tile.ColumnIndex + 1}"));
                }
            }

            if (images.Count == 0)
                throw new InvalidOperationException("Nothing to export — no pages are loaded.");

            // ExportAsImageFiles does the actual (potentially slow, for many large pages) file
            // writing — offloaded to a background thread so the UI thread stays free to actually
            // render this status text while it runs, rather than blocking synchronously until
            // every file is done and only ever showing the "finished" message.
            StatusText = $"Exporting {images.Count} image(s) to {destinationPath}...";
            await Task.Run(() => ExportAsImageFiles(images, destinationPath, format));
            StatusText = $"Exported {images.Count} image(s) to {Path.GetDirectoryName(destinationPath)}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private readonly record struct ExportImage(int Width, int Height, int Dpi, byte[] PixelData, string Label);

    private static BitmapSource ExportImageToBitmapSource(ExportImage image) =>
        BitmapSource.Create(image.Width, image.Height, image.Dpi, image.Dpi, PixelFormats.Bgra32, null, image.PixelData, image.Width * 4);

    /// <summary>One file per image, at its true actual pixel size — no PrintLayoutMath/paper-size
    /// scaling involved at all, since PNG/JPG have no inherent concept of a "page". PNG compression
    /// level and JPG quality both come from ExportQuality (Settings > General) — WPF's own
    /// PngBitmapEncoder has no compression-level control, so PNG goes via OpenCvSharp instead
    /// (already a project dependency), which does; JPG stays on WPF's JpegBitmapEncoder, which
    /// <summary>One file per image, at its true actual pixel size — no PrintLayoutMath/paper-size
    /// scaling involved at all, since PNG/JPG have no inherent concept of a "page". PNG compression
    /// level and JPG quality both come from ExportQuality (Settings > General) — WPF's own
    /// PngBitmapEncoder has no compression-level control, so PNG goes via OpenCvSharp instead
    /// (already a project dependency), which does; JPG stays on WPF's JpegBitmapEncoder, which
    /// already supports a quality level directly. If LinkExportsToJobs is on, each written file's
    /// path is recorded into the CURRENT JOB's OWN source-images.json (JobSourceService.
    /// AddSourcePath) — the same list, and the same lookup, Open Image(s)'s duplicate check already
    /// uses for images a job was originally created from. Deliberately NOT a sidecar file written
    /// next to the exported image: keeping every bit of this bookkeeping inside the job's own
    /// AppData folder means Export never adds so much as one extra file to wherever the person
    /// chose to save their export. No-ops if no job is currently loaded, since there'd be nothing
    /// to link to.</summary>
    private void ExportAsImageFiles(List<ExportImage> images, string destinationPath, string format)
    {
        string directory = Path.GetDirectoryName(destinationPath) is { Length: > 0 } dir ? dir : ".";
        string baseName = Path.GetFileNameWithoutExtension(destinationPath);
        bool isJpg = string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase);
        string extension = isJpg ? ".jpg" : ".png";

        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            string fileName = images.Count == 1 ? $"{baseName}{extension}" : $"{baseName}_{image.Label}{extension}";
            string filePath = Path.Combine(directory, fileName);

            if (isJpg)
            {
                var bitmap = ExportImageToBitmapSource(image);
                var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(ExportQuality.JpgQuality, 1, 100) };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(filePath);
                encoder.Save(stream);
            }
            else
            {
                // Alpha dropped before encoding (BGRA -> BGR) — always fully opaque for these
                // images, so it carries no information; removing it shrinks the raw data 25%
                // before compression even starts. Safe and transparent to the rest of the app for
                // the same reason it is in the Watcher's own capture save (see
                // XpsCaptureAndConvertService.SavePngWithConfiguredCompression's doc comment):
                // anything that re-reads this file upgrades back to BGRA32 on load regardless of
                // the source's actual channel count.
                using var bgraMat = new OpenCvSharp.Mat(image.Height, image.Width, OpenCvSharp.MatType.CV_8UC4);
                System.Runtime.InteropServices.Marshal.Copy(image.PixelData, 0, bgraMat.Data, image.PixelData.Length);

                using var bgrMat = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(bgraMat, bgrMat, OpenCvSharp.ColorConversionCodes.BGRA2BGR);

                OpenCvSharp.Cv2.ImWrite(filePath, bgrMat,
                    new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.PngCompression, Math.Clamp(ExportQuality.PngCompressionLevel, 0, 9)));
            }

            if (LinkExportsToJobs && !string.IsNullOrEmpty(_jobId))
                JobSourceService.AddSourcePath(CleanPrintPaths.JobFolder(_jobId), filePath);
        }
    }

    // ---- Bitmap conversion ----

    /// <summary>Sets _originalPageWidth/Height from whichever page (raw/whitened) is currently
    /// selected by ShowBefore — independent of PreviewImage, which becomes the smaller,
    /// recomposited bitmap once any Hide Row/Column is active. Must run before polygons are loaded
    /// for a page (their own Freeze/Hide validation checks against these), and before
    /// RefreshPreviewImage recomposites using the resulting hidden ranges.</summary>
    private void UpdateOriginalPageDimensions()
    {
        var page = ShowBefore ? _currentRawPage : _currentWhitenedPage;
        if (page is null)
        {
            _originalPageWidth = 0;
            _originalPageHeight = 0;
            return;
        }
        _originalPageWidth = page.Width * 96.0 / page.Dpi;
        _originalPageHeight = page.Height * 96.0 / page.Dpi;
    }

    private void RefreshPreviewImage()
    {
        var page = ShowBefore ? _currentRawPage : _currentWhitenedPage;
        if (page is null) return;

        var bitmap = PageToBitmapSource(page);
        // Row bands first, then column bands on the result — this ordering is a deliberate
        // simplification: if both a row and a column happen to be hidden simultaneously, the
        // composition order can matter at the pixel level, but for the common case (independent
        // row/column hides that don't overlap) the result is correct either way.
        bitmap = RemoveHiddenBands(bitmap, _hiddenYRanges, isRowWise: true);
        bitmap = RemoveHiddenBands(bitmap, _hiddenXRanges, isRowWise: false);
        PreviewImage = bitmap;
    }

    private static BitmapSource PageToBitmapSource(CapturedPage page)
    {
        var bmp = BitmapSource.Create(
            page.Width, page.Height, page.Dpi, page.Dpi,
            PixelFormats.Bgra32, null, page.PixelData, page.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource TileToBitmapSource(SplitTile tile)
    {
        var bmp = BitmapSource.Create(
            tile.Width, tile.Height, 96, 96,
            PixelFormats.Bgra32, null, tile.PixelData, tile.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---- Split preview mockup rendering ----

    private const double MockupPageBoxWidth = 150;
    private const double MockupPageBoxHeight = 170;
    private const double DefaultPaperWidthInches = 8.27;  // A4, used only if no printer/paper is selected yet
    private const double DefaultPaperHeightInches = 11.69;

    /// <summary>
    /// Rebuilds SplitPreviewTiles from the currently-split tiles (_currentSplitTiles), rendering
    /// each as an accurate mockup of the actual printed page — same paper size, orientation,
    /// margins, and fit/scale math (via PrintLayoutMath) that PrintingService uses at print time.
    /// Cheap to call on every print-setting change since it never re-splits or re-whitens.
    /// </summary>
    private void RefreshSplitPreviewMockups()
    {
        if (_currentSplitTiles.Count == 0)
            return; // nothing split yet — nothing to refresh

        SplitPreviewTiles.Clear();
        for (int i = 0; i < _currentSplitTiles.Count; i++)
        {
            SplitPreviewTiles.Add(new TilePreviewItem
            {
                Image = BuildPageMockupBitmap(_currentSplitTiles[i]),
                Label = _currentSplitTileLabels[i],
            });
        }
    }

    /// <summary>
    /// Renders a tile the way it will actually print: a page-shaped rectangle sized to the
    /// selected paper size and orientation, with the tile positioned inside the margin area per
    /// FitToPage/ScalePercent — using the exact same PrintLayoutMath.ComputeDrawRect calculation
    /// PrintingService uses, so this preview can never drift from the real printed output.
    /// </summary>
    private BitmapSource BuildPageMockupBitmap(SplitTile tile)
    {
        double paperWidthInches = SelectedPaperSizeInfo?.WidthInches ?? DefaultPaperWidthInches;
        double paperHeightInches = SelectedPaperSizeInfo?.HeightInches ?? DefaultPaperHeightInches;
        if (IsLandscape)
            (paperWidthInches, paperHeightInches) = (paperHeightInches, paperWidthInches);

        // Fit the whole page rectangle within a fixed thumbnail box so every gallery item has the
        // same outer size regardless of paper orientation — only the page rectangle's own aspect
        // ratio (and what's drawn inside it) changes.
        double pxPerInch = Math.Min(MockupPageBoxWidth / paperWidthInches, MockupPageBoxHeight / paperHeightInches);
        double pageWidthPx = paperWidthInches * pxPerInch;
        double pageHeightPx = paperHeightInches * pxPerInch;

        double availWidthInches = Math.Max(0.1, paperWidthInches - MarginLeftInches - MarginRightInches);
        double availHeightInches = Math.Max(0.1, paperHeightInches - MarginTopInches - MarginBottomInches);

        var rect = PrintLayoutMath.ComputeDrawRect(
            availableWidthInches: availWidthInches,
            availableHeightInches: availHeightInches,
            tileWidthPixels: tile.Width,
            tileHeightPixels: tile.Height,
            tileDpi: tile.Dpi,
            fitToPage: FitToPage,
            scalePercent: ScalePercent);

        double contentOriginXPx = MarginLeftInches * pxPerInch;
        double contentOriginYPx = MarginTopInches * pxPerInch;
        var imageRectPx = new Rect(
            contentOriginXPx + rect.XInches * pxPerInch,
            contentOriginYPx + rect.YInches * pxPerInch,
            Math.Max(0.5, rect.WidthInches * pxPerInch),
            Math.Max(0.5, rect.HeightInches * pxPerInch));

        var tileBitmap = TileToBitmapSource(tile);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var pageRect = new Rect(0, 0, pageWidthPx, pageHeightPx);
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), pageRect);

            // Content clipped to the page — if FitToPage is off and the tile overflows the
            // margins (intentional per PrintOptions.FitToPage docs), it should visibly run off
            // the page edge in the preview too, matching what will actually happen at print time.
            dc.PushClip(new RectangleGeometry(pageRect));
            dc.DrawImage(tileBitmap, imageRectPx);
            dc.Pop();
        }

        int pixelWidth = Math.Max(1, (int)Math.Round(pageWidthPx));
        int pixelHeight = Math.Max(1, (int)Math.Round(pageHeightPx));
        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Minimal observable collection wrapper to avoid pulling in extra dependencies.</summary>
public sealed class ObservableCollectionSlim<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public ObservableCollectionSlim() { }
    public ObservableCollectionSlim(IEnumerable<T> items) : base(items) { }
}
