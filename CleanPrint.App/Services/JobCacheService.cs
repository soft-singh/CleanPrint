using System;
using System.IO;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>How many jobs the job cache folder holds and how many bytes they take up in total.</summary>
public readonly record struct JobCacheUsage(int JobCount, long TotalBytes)
{
    public double TotalMegabytes => TotalBytes / (1024.0 * 1024.0);
}

/// <summary>
/// Measures the job cache — every folder directly under CleanPrintPaths.JobsRoot is one job, and a
/// job's size is every file anywhere beneath it (page images plus its small JSON sidecars).
/// Purely read-only: deciding what to do about a full cache is left to the person, via the Open
/// Job dialog.
/// </summary>
public static class JobCacheService
{
    public static JobCacheUsage Measure()
    {
        if (!Directory.Exists(CleanPrintPaths.JobsRoot)) return default;

        int jobCount = 0;
        long totalBytes = 0;
        foreach (var jobFolder in Directory.EnumerateDirectories(CleanPrintPaths.JobsRoot))
        {
            jobCount++;
            totalBytes += GetFolderSize(jobFolder);
        }
        return new JobCacheUsage(jobCount, totalBytes);
    }

    /// <summary>Total size in bytes of every file under the folder. A folder that disappears or
    /// can't be read part-way through (a job being deleted or written at the same moment) counts
    /// for whatever was read so far rather than failing the whole measurement.</summary>
    public static long GetFolderSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += file.Length; }
                catch (IOException) { /* file vanished between enumeration and stat — skip it */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Compact human-readable size for list rows, e.g. "512 KB", "34.2 MB", "1.8 GB".</summary>
    public static string FormatSize(long bytes)
    {
        const double kb = 1024.0, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:N1} GB";
        if (bytes >= mb) return $"{bytes / mb:N1} MB";
        return $"{Math.Max(0, bytes / kb):N0} KB";
    }
}
