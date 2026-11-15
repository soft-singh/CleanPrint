using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>Loads/saves source-images.json inside a job's own folder, and finds an existing job
/// for a given image path — see JobSourceInfo's own doc comment for why this exists. Despite the
/// name, this now covers both "images this job was originally created from" (Open Image(s)) AND
/// "images this job's pages were later exported to" (see AddSourcePath) — both are, from a lookup
/// perspective, exactly the same question: "does this file path already correspond to an existing
/// job?" Keeping them in one list means Open Image(s)'s duplicate check finds either kind without
/// needing a second, parallel mechanism, and — just as importantly — without ever writing anything
/// into the person's own folders: every bit of this bookkeeping lives inside the job's own AppData
/// folder, the same as it always has for source images.</summary>
public static class JobSourceService
{
    private const string FileName = "source-images.json";

    public static JobSourceInfo? Load(string jobFolder)
    {
        try
        {
            string path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<JobSourceInfo>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string jobFolder, JobSourceInfo info)
    {
        try
        {
            Directory.CreateDirectory(jobFolder);
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(jobFolder, FileName), json);
        }
        catch
        {
            // best effort — losing this isn't worth crashing over; it just means the duplicate
            // check won't recognize this job next time
        }
    }

    /// <summary>Appends one more path to a job's source-images.json (creating it if this job has
    /// none yet) and records its current LastWriteTimeUtc as the baseline HasSourceImageChanged
    /// compares against later — used when linking an exported image back to the job it came from
    /// (see MainViewModel.ExportAsImageFiles), so that export ends up discoverable by Open Image(s)
    /// the exact same way an original source image is, rather than through a separate file written
    /// next to the export destination.</summary>
    public static void AddSourcePath(string jobFolder, string imagePath) => RecordSourcePath(jobFolder, imagePath);

    /// <summary>Records a source path together with its current LastWriteTimeUtc — used for the
    /// original "Open Image(s)" job creation (each selected file) and, via AddSourcePath, for the
    /// export-link case. Re-recording an already-present path refreshes its timestamp rather than
    /// no-op'ing, since the underlying file may have changed since it was first linked.</summary>
    public static void RecordSourcePath(string jobFolder, string imagePath)
    {
        var info = Load(jobFolder) ?? new JobSourceInfo();
        if (!info.SourceImagePaths.Any(p => string.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase)))
            info.SourceImagePaths.Add(imagePath);

        try
        {
            info.LastKnownWriteTimesUtc[imagePath] = File.GetLastWriteTimeUtc(imagePath);
        }
        catch
        {
            // best effort — if the timestamp can't be read, HasSourceImageChanged simply has no
            // baseline for this path, same as an older job saved before this tracking existed
        }

        Save(jobFolder, info);
    }

    /// <summary>True if imagePath's current LastWriteTimeUtc differs from what was recorded when it
    /// was linked to this job (see RecordSourcePath) — false, NOT "changed", when there's no
    /// recorded baseline at all (an older job, or a path never linked via RecordSourcePath), since
    /// there's nothing meaningful to compare against and treating "unknown" as "changed" would
    /// falsely flag every pre-existing job the very first time this shipped.</summary>
    public static bool HasSourceImageChanged(string jobFolder, string imagePath)
    {
        var info = Load(jobFolder);
        if (info is null) return false;
        if (!info.LastKnownWriteTimesUtc.TryGetValue(imagePath, out var recorded)) return false;

        try
        {
            return File.GetLastWriteTimeUtc(imagePath) != recorded;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Scans every job folder for one whose source-images.json lists this exact path
    /// (case-insensitive, full path) — returns that job's folder path, or null if none matches.
    /// Matches by full path rather than filename alone, since two different folders could easily
    /// have same-named files that aren't actually the same image.</summary>
    public static string? FindJobForImage(string imagePath)
    {
        if (!Directory.Exists(CleanPrintPaths.JobsRoot)) return null;

        foreach (var jobFolder in Directory.GetDirectories(CleanPrintPaths.JobsRoot))
        {
            var info = Load(jobFolder);
            if (info is null) continue;
            if (info.SourceImagePaths.Any(p => string.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase)))
                return jobFolder;
        }

        return null;
    }
}
