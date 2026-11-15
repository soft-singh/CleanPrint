namespace CleanPrint.Core.Models;

/// <summary>
/// Per-job state that should be remembered across sessions once a job has actually been worked on
/// — which effects/preset were applied, and what zoom level — distinct from
/// AppSettings.DefaultPresetName, which only governs what a job starts with the VERY FIRST time
/// it's ever opened. Stored as job-state.json inside each job's own folder (see
/// CleanPrint.App.Services.JobViewStateService), alongside polygon-annotations.json and the
/// page_*.png files — reopening a previously-worked-on job restores this instead of re-applying
/// the default-preset logic, so effects/zoom don't need to be redone by hand every time.
/// </summary>
public sealed class JobViewState
{
    public ImageEffectSettings? EffectSettings { get; set; }
    public double Zoom { get; set; } = 1.0;
}
