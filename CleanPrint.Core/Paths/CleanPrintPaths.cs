using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanPrint.Core.Paths;

/// <summary>
/// Single source of truth for every path and name shared across Setup, Watcher, and App.
/// Keeping this in Core means the printer name or capture path can never drift out of sync
/// between the three executables.
/// </summary>
public static class CleanPrintPaths
{
    /// <summary>Display name shown in Windows' printer list.</summary>
    public const string PrinterName = "Clean Print";

    /// <summary>Name of the scheduled task that keeps the Watcher running without admin rights.</summary>
    public const string WatcherTaskName = "CleanPrintWatcher";

    public static string LocalAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanPrint");

    /// <summary>Fixed file the printer port writes to. The Watcher watches this exact path.</summary>
    public static string CaptureFilePath => Path.Combine(LocalAppDataRoot, "Capture", "incoming.xps");

    /// <summary>Root folder under which each print job gets its own subfolder of page PNGs.</summary>
    public static string JobsRoot => Path.Combine(LocalAppDataRoot, "Jobs");

    /// <summary>Fixed location for named Effects presets (see EffectPresetService in CleanPrint.App)
    /// — one JSON file per preset, named after the preset itself.</summary>
    public static string PresetsRoot => Path.Combine(LocalAppDataRoot, "Presets");

    public static string LogsRoot => Path.Combine(LocalAppDataRoot, "logs");

    /// <summary>
    /// Path to CleanPrint.App.exe, assumed to sit alongside whichever executable is currently
    /// running (Setup installs Watcher and App into the same folder). Using the running process's
    /// own directory rather than a hardcoded "Program Files" path also makes local dev/testing
    /// work without an installer.
    /// </summary>
    public static string AppExePath => Path.Combine(AppContext.BaseDirectory, "CleanPrint.App.exe");

    // ---- Private mode: jobs that are not kept ------------------------------------------------------------------------------------------------------------------------------
    // While private mode is on, jobs live OUTSIDE the job cache (the Jobs folder), in the user's temp folder, under an id that starts with "private-". Everything that has a job id asks
    // JobFolder(id) where that job is, so a private job is found in its temp folder and an ordinary one in Jobs, with no other special-casing. Private jobs never appear in Open Job or the
    // cache figures (those list Jobs only) and are deleted when you move to another job, and when CleanPrint closes or next starts.

    /// <summary>What a private job's id (and so its folder name) starts with.</summary>
    public const string PrivateJobPrefix = "private-";

    /// <summary>Where private jobs live: the temp folder, not the job cache, so a crash or an antivirus sweep can never leave them in the cache.</summary>
    public static string PrivateJobsRoot => Path.Combine(Path.GetTempPath(), "CleanPrint-Private");

    /// <summary>A marker file the App keeps in existence while private mode is on, so the Watcher (a separate process) knows not to leave a trace of a job captured meanwhile.</summary>
    public static string PrivateModeFlagPath => Path.Combine(LocalAppDataRoot, "private-mode.flag");

    public static bool IsPrivateJobId(string? jobId) =>
        jobId is not null && jobId.StartsWith(PrivateJobPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder of the job with this id: in the private area for a private job, otherwise in the job cache.</summary>
    public static string JobFolder(string jobId) =>
        Path.Combine(IsPrivateJobId(jobId) ? PrivateJobsRoot : JobsRoot, jobId);

    /// <summary>
    /// Makes a new, empty job folder for <paramref name="baseId"/> and returns its path: in the job cache, or — for a private job — in the private area with the private prefix on its id.
    /// If a folder of that name exists (two jobs made in the same second) a number is added.
    /// </summary>
    public static string NewJobFolder(string baseId, bool isPrivate)
    {
        string id = isPrivate && !IsPrivateJobId(baseId) ? PrivateJobPrefix + baseId : baseId;
        string root = isPrivate ? PrivateJobsRoot : JobsRoot;
        string folder = Path.Combine(root, id);
        for (int n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(root, $"{id}-{n}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CaptureFilePath)!);
        Directory.CreateDirectory(JobsRoot);
        Directory.CreateDirectory(PresetsRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    /// <summary>Every page file in a job folder, in page order, regardless of whether it was saved
    /// as PNG (the default) or JPG (opt-in lossy capture — see
    /// ExportQualitySettings.UseJpegForCapture). A given job's pages are always all one format or
    /// the other (decided once, at capture time) — never a mix — but this doesn't assume which, so
    /// every caller that needs "this job's pages" gets the right answer regardless. Returns an
    /// empty list (not an error) if the folder doesn't exist.</summary>
    public static IReadOnlyList<string> GetJobPageFiles(string jobFolder)
    {
        if (!Directory.Exists(jobFolder)) return Array.Empty<string>();

        return Directory.GetFiles(jobFolder, "page_*.png")
            .Concat(Directory.GetFiles(jobFolder, "page_*.jpg"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
