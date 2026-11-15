using System.IO;
using System.Text.Json;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>Loads/saves job-state.json inside a job's own folder — see JobViewState's doc comment
/// for what this is and why it's separate from AppSettings.DefaultPresetName.</summary>
public static class JobViewStateService
{
    private const string FileName = "job-state.json";

    /// <summary>Null if this job has never had its state saved (e.g. never opened before this
    /// feature existed, or genuinely brand new) or the file is unreadable — the caller falls back
    /// to the default-preset logic in that case.</summary>
    public static JobViewState? Load(string jobFolder)
    {
        try
        {
            string path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<JobViewState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string jobFolder, JobViewState state)
    {
        try
        {
            if (!Directory.Exists(jobFolder)) return; // job folder gone (e.g. deleted mid-session) — nothing to save into
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(jobFolder, FileName), json);
        }
        catch
        {
            // best effort — losing this isn't worth crashing over
        }
    }
}
