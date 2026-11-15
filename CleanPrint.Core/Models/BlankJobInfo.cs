namespace CleanPrint.Core.Models;

/// <summary>
/// Marks a job as a BLANK job — one made with "New Blank Job" rather than captured from a print or opened from an image — and records the page it
/// was made with, so that pages added later match it exactly. Stored as blank-job.json inside the job's own folder (see BlankJobInfoService).
/// Its presence is what turns on the page controls (add / insert / delete page): a captured job's pages come from the source it was made from and
/// aren't something to add to or remove from, so they never get this file.
/// </summary>
public sealed class BlankJobInfo
{
    /// <summary>"A4", "Letter", "Custom" … — only for display (the job browser, status messages); the pixel size below is what counts.</summary>
    public string PaperName { get; set; } = "";

    public bool Landscape { get; set; }

    /// <summary>The page size in pixels, after orientation — every page of the job is exactly this.</summary>
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }

    public int Dpi { get; set; } = 300;
}
