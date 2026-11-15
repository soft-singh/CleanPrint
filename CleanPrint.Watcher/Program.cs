using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Export;
using CleanPrint.Core.Logging;
using CleanPrint.Core.Models;
using CleanPrint.Core.Services;
using CleanPrint.Core.Paths;
using CleanPrint.Watcher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CleanPrint.Watcher;

internal static class Program
{
    /// <summary>Prevents two Watcher instances from both binding the capture folder (e.g. if the
    /// Scheduled Task somehow fires twice, or the user double-clicks the exe while it's already running).</summary>
    private const string SingleInstanceMutexName = "Global\\CleanPrintWatcher.SingleInstance";

    // WPF's rendering APIs (RenderTargetBitmap, FixedPage layout) require an STA thread even
    // when no window/Application is ever created.
    [STAThread]
    private static async Task Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Another Watcher is already running — nothing to do, exit quietly (no console to write to anyway).
            return;
        }

        CleanPrintPaths.EnsureDirectoriesExist();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .AddProvider(new FileLoggerProvider(Path.Combine(CleanPrintPaths.LogsRoot, "watcher.log")))
            .SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<XpsCaptureAndConvertService>();
        services.AddSingleton<AppNotifier>();

        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<XpsCaptureAndConvertService>>();
        var captureService = provider.GetRequiredService<XpsCaptureAndConvertService>();
        var appNotifier = provider.GetRequiredService<AppNotifier>();

        logger.LogInformation("CleanPrint.Watcher starting (PID {Pid}).", Environment.ProcessId);

        captureService.JobReady += async (_, e) =>
        {
            try
            {
                // PRIVATE MODE: the App keeps a marker file while it is in private mode. If it is genuine (the App is actually running — a crash can leave a stale file) a job captured
                // now must leave no trace.
                bool privateMode = File.Exists(CleanPrintPaths.PrivateModeFlagPath) && AppNotifier.IsAppRunning();

                // SILENT EXPORT: save the pages to the chosen folder and never open the window.
                var silent = SilentExportSettingsService.Load();
                if (silent is { Enabled: true })
                {
                    var quality = ExportQualitySettingsService.Load() ?? new ExportQualitySettings();
                    var files = SilentImageExporter.Export(e.JobFolder, e.JobId, silent, quality, logger);

                    // The job normally stays in the cache (so it can still be opened later). Not if the user asked to drop it — or in private mode, where nothing may be kept. And never
                    // when the export produced nothing: then the job is all the user has.
                    if (files.Count > 0 && (!silent.KeepJob || privateMode))
                    {
                        try { Directory.Delete(e.JobFolder, recursive: true); }
                        catch (Exception ex) { logger.LogWarning(ex, "Could not remove job folder {Folder} after the silent export.", e.JobFolder); }
                    }
                    logger.LogInformation("Job {JobId} exported silently ({Files} file(s)); the App was not opened.", e.JobId, files.Count);
                    if (files.Count > 0) return;
                    logger.LogWarning("Silent export produced no files for job {JobId}; opening it in the App instead.", e.JobId);
                }

                logger.LogInformation("Job {JobId} ready ({PageCount} page(s)) — notifying/launching App.", e.JobId, e.PageCount);
                await appNotifier.NotifyOrLaunchAppAsync(e.JobFolder);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify or launch CleanPrint.App for job {JobId}.", e.JobId);
            }
        };

        using var shutdownCts = new CancellationTokenSource();

        // Scheduled Task termination / logoff sends a normal process exit; make sure we release
        // the file watcher cleanly rather than relying on finalizers.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.LogInformation("CleanPrint.Watcher shutting down.");
            shutdownCts.Cancel();
            captureService.StopAsync().GetAwaiter().GetResult();
        };

        await captureService.StartAsync(shutdownCts.Token);

        // Headless process: just stay alive, watching, until the OS terminates us (logoff, task
        // scheduler stop, or an explicit kill). No UI, no console, nothing else to do here.
        try
        {
            await Task.Delay(Timeout.Infinite, shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }
}
