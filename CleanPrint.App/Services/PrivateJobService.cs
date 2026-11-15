using System;
using System.IO;
using System.Threading.Tasks;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>
/// PRIVATE MODE — like a browser's private window, but for jobs. While it is on, jobs are not kept in the job cache: a job captured meanwhile is moved out of the cache into a private
/// area in the temp folder (see <see cref="CleanPrintPaths.PrivateJobsRoot"/>), a blank job or opened image is made there to begin with, and each such job is deleted when you move on to
/// another job, when CleanPrint closes, and (for any a crash left behind) when it next starts. Private jobs never appear in Open Job or in the cache figures, since those list the cache only.
///
/// A job's folder is found through <see cref="CleanPrintPaths.JobFolder"/>, which knows the private ones by their id prefix, so the rest of the app needs no special cases.
/// </summary>
public static class PrivateJobService
{
    /// <summary>
    /// Keeps a marker file in existence while private mode is on. The Watcher — a separate process — looks for it, so that a job it captures while you are in private mode (and with silent export
    /// on) leaves nothing behind. (The Watcher also checks the App is really running, in case a crash leaves the file behind.)
    /// </summary>
    public static void SetModeFlag(bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(CleanPrintPaths.LocalAppDataRoot);
                File.WriteAllText(CleanPrintPaths.PrivateModeFlagPath, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(CleanPrintPaths.PrivateModeFlagPath))
            {
                File.Delete(CleanPrintPaths.PrivateModeFlagPath);
            }
        }
        catch
        {
            // best effort: the worst case is the Watcher treating a job as ordinary
        }
    }

    /// <summary>
    /// Takes a job the Watcher left in the job cache out of it and into the private area, and returns its new folder. It is a rename when both are on the same drive (so the pages are never
    /// copied, and the job is in the cache for only an instant); otherwise it is copied across and the original deleted. Throws if it cannot be done — the caller decides what to tell the user.
    /// </summary>
    public static string AdoptIntoPrivate(string jobFolder)
    {
        string name = Path.GetFileName(jobFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string destination = CleanPrintPaths.NewJobFolder(name, isPrivate: true);   // makes an empty folder with a free name
        Directory.Delete(destination);                                             // …so the original can be moved onto that name

        try
        {
            Directory.Move(jobFolder, destination);
        }
        catch (IOException)
        {
            CopyDirectory(jobFolder, destination);
            Directory.Delete(jobFolder, recursive: true);
        }
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Deletes a private job's folder in the background. A file may still be held for a moment by the preview that has just stopped showing it, so it tries again for several seconds;
    /// whatever survives is swept up by <see cref="PurgeAll"/> when CleanPrint closes or next starts. Only ever touches the private area.
    /// </summary>
    public static void DeleteFolder(string folder)
    {
        if (!IsInsidePrivateArea(folder)) return;   // never delete anything outside the private area, whatever it is asked
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!Directory.Exists(folder)) return;
                    Directory.Delete(folder, recursive: true);
                    return;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    /// <summary>Deletes every private job (the whole private area). Called when CleanPrint closes and when it starts.</summary>
    public static void PurgeAll()
    {
        try
        {
            string root = CleanPrintPaths.PrivateJobsRoot;
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.GetDirectories(root))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch { /* held by something: it is in the temp folder and will go next time */ }
            }
        }
        catch
        {
            // best effort
        }
    }

    private static bool IsInsidePrivateArea(string folder)
    {
        string root = Path.GetFullPath(CleanPrintPaths.PrivateJobsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
