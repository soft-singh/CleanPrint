using CleanPrint.App.Theming;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CleanPrint.App.Services;
using CleanPrint.App.Services.ImageProcessing;
using CleanPrint.App.Services.PageSplitting;
using CleanPrint.App.Services.Printing;
using CleanPrint.App.ViewModels;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Ipc;
using CleanPrint.Core.Logging;
using CleanPrint.Core.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CleanPrint.App;

public partial class App : Application
{
    /// <summary>Must match the name used nowhere else — this is App-specific, distinct from the
    /// Watcher's own single-instance mutex ("Global\CleanPrintWatcher.SingleInstance").</summary>
    private const string SingleInstanceMutexName = "Global\\CleanPrintApp.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _pipeListenerCts;
    private MainWindow? _mainWindow;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Another instance already owns the UI. Forward our job-folder argument (if any) to
            // it over the pipe, then exit immediately without ever showing a window — this covers
            // the case where AppNotifier decided (incorrectly, e.g. due to a race) that no
            // instance was running and launched a second one anyway.
            var jobFolder = e.Args.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(jobFolder))
                await AppLaunch.TryNotifyRunningInstanceAsync(jobFolder);

            Shutdown();
            return;
        }

        CleanPrintPaths.EnsureDirectoriesExist();

        // Private mode never survives a restart: sweep up anything a crash left behind, and drop a stale marker file.
        PrivateJobService.PurgeAll();
        PrivateJobService.SetModeFlag(false);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .AddProvider(new FileLoggerProvider(Path.Combine(CleanPrintPaths.LogsRoot, "app.log")))
            .SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IImageProcessingService, BackgroundWhiteningService>();
        services.AddSingleton<IPageSplittingService, PageSplittingService>();
        services.AddSingleton<IPrintingService, PrintingService>();
        services.AddSingleton<PolygonAnnotationStore>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("CleanPrint.App starting (PID {Pid}).", Environment.ProcessId);

        // Until now an unhandled exception simply ended the program, with nothing written down. These record the full exception (with
        // its stack trace) in app.log. A failure on the UI thread — a bad click, a binding gone wrong — is then reported and the program
        // carries on (annotations are saved as they are edited, so nothing is lost); anything on another thread is only logged.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating}).", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        ThemeManager.SetIconLightness(_mainWindow.ViewModel.IconLightness);
        ThemeManager.Apply(_mainWindow.ViewModel.ThemeName); // reflects whatever was persisted, before the window is ever shown
        _mainWindow.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IconLightness))
                ThemeManager.SetIconLightness(_mainWindow.ViewModel.IconLightness);   // live: the icons re-colour as the slider moves
            else if (args.PropertyName == nameof(MainViewModel.ThemeName))
                ThemeManager.Apply(_mainWindow.ViewModel.ThemeName);   // live: every window follows at once
        };
        _mainWindow.Show();

        // Listen for the rest of this process's lifetime: the Watcher notifies us here for every
        // job after the first, and a duplicate-instance launch (handled above) also lands here.
        _pipeListenerCts = new CancellationTokenSource();
        _ = AppLaunch.ListenForJobsAsync(
            jobFolder => Dispatcher.Invoke(() => LoadJobAndActivate(jobFolder)),
            _pipeListenerCts.Token);

        // If the Watcher launched us fresh (no running instance to notify), the job folder
        // arrives as our own command-line argument instead of over the pipe.
        var initialJobFolder = e.Args.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(initialJobFolder))
            LoadJobAndActivate(initialJobFolder);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread.");
        e.Handled = true; // keep running: the alternative is losing the whole session to one failed action
        MessageBox.Show(
            "Something went wrong, but Clean Print is still running.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"The details were written to:\n{Path.Combine(CleanPrintPaths.LogsRoot, "app.log")}",
            "Clean Print", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void LoadJobAndActivate(string jobFolder)
    {
        // In private mode a job the Watcher has just captured is taken out of the job cache before it is shown — it must not stay there.
        if (_mainWindow?.ViewModel.IsPrivateMode == true && IsInJobCache(jobFolder))
        {
            try
            {
                jobFolder = PrivateJobService.AdoptIntoPrivate(jobFolder);
                _logger?.LogInformation("Private mode: moved the captured job out of the job cache to {Folder}", jobFolder);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Private mode: could not move the captured job out of the job cache; it stays there.");
                _mainWindow.ViewModel.ShowStatus("Private mode could not keep this job out of the job cache (it could not be moved); it was saved like an ordinary job.");
            }
        }

        _logger?.LogInformation("Loading job from {Folder}", jobFolder);
        _mainWindow?.ViewModel.LoadJob(jobFolder);
        _mainWindow?.BringToFront();
    }

    private static bool IsInJobCache(string jobFolder)
    {
        try
        {
            string parent = Path.GetFullPath(Path.GetDirectoryName(jobFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "");
            return string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(CleanPrintPaths.JobsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Private jobs do not outlive CleanPrint.
        PrivateJobService.PurgeAll();
        PrivateJobService.SetModeFlag(false);

        _pipeListenerCts?.Cancel();
        _serviceProvider?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
