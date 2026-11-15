using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Ipc;
using CleanPrint.Core.Paths;
using Microsoft.Extensions.Logging;

namespace CleanPrint.Watcher.Services;

/// <summary>
/// Hands a finished job off to CleanPrint.App: notifies a running instance over the named pipe,
/// or launches a fresh one if none is listening.
///
/// A single quick pipe-connect attempt (as in the raw <see cref="AppLaunch"/> helper) is risky on
/// its own: if the App is running but briefly busy re-arming its pipe listener between two jobs
/// arriving close together, a single failed attempt would wrongly conclude "not running" and
/// launch a second, duplicate App instance. This retries with backoff, and additionally checks
/// the process list before giving up, so a duplicate is only ever launched when the App is
/// genuinely not running.
/// </summary>
public sealed class AppNotifier
{
    private static readonly int[] RetryDelaysMs = { 200, 400, 800 };

    private readonly ILogger<AppNotifier> _logger;

    public AppNotifier(ILogger<AppNotifier> logger) => _logger = logger;

    public async Task NotifyOrLaunchAppAsync(string jobFolder, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < RetryDelaysMs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (await AppLaunch.TryNotifyRunningInstanceAsync(jobFolder))
            {
                _logger.LogInformation("Notified running CleanPrint.App instance of job at {Folder}.", jobFolder);
                return;
            }

            // Pipe connect failed. Before assuming nothing is running, check whether an App
            // process actually exists — if it does, the pipe listener is probably just mid-cycle
            // between accepting connections, so back off and retry rather than duplicate-launch.
            if (IsAppProcessRunning())
            {
                _logger.LogDebug(
                    "CleanPrint.App process is running but didn't accept the pipe connection (attempt {Attempt}); retrying.",
                    attempt + 1);
                await Task.Delay(RetryDelaysMs[attempt], ct);
                continue;
            }

            break; // no process found running — safe to launch a fresh one
        }

        await LaunchFreshInstanceAsync(jobFolder);
    }

    /// <summary>True if a CleanPrint.App process exists. Public so the job-ready handler can tell whether a private-mode marker file is genuine (the App is there to have written it) or a stale
    /// leftover from a crash.</summary>
    public static bool IsAppRunning() => IsAppProcessRunning();

    private static bool IsAppProcessRunning()
    {
        var processName = Path.GetFileNameWithoutExtension(CleanPrintPaths.AppExePath);
        return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
    }

    private Task LaunchFreshInstanceAsync(string jobFolder)
    {
        if (!File.Exists(CleanPrintPaths.AppExePath))
        {
            // Expected until Step 4/5 build the App itself — log clearly rather than throwing,
            // since a missing App shouldn't take the Watcher down.
            _logger.LogWarning(
                "CleanPrint.App.exe not found at {Path}. Job PNGs are saved at {Folder}, but no App instance could be launched to display them.",
                CleanPrintPaths.AppExePath, jobFolder);
            return Task.CompletedTask;
        }

        _logger.LogInformation("No running CleanPrint.App instance found — launching a new one for job at {Folder}.", jobFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CleanPrintPaths.AppExePath, $"\"{jobFolder}\"")
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}
