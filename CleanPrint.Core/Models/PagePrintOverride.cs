namespace CleanPrint.Core.Models;

/// <summary>
/// The page settings one page of a print uses INSTEAD of the common ones: paper size, orientation, and whether it is fitted to the page or printed at a set percentage. A page has one
/// only while "different settings for this page" is ticked for it; the values are copied from the common settings at that moment, so changing the common settings afterwards doesn't
/// disturb a page that was set apart. Margins, copies and page numbers stay common to the whole job.
/// </summary>
public sealed class PagePrintOverride
{
    public string? PaperSizeName { get; set; }
    public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;
    public bool FitToPage { get; set; } = true;
    public double ScalePercent { get; set; } = 100;

    public PagePrintOverride Clone() => new()
    {
        PaperSizeName = PaperSizeName, Orientation = Orientation, FitToPage = FitToPage, ScalePercent = ScalePercent,
    };

    /// <summary>A starting point for a page being set apart: exactly the common settings.</summary>
    public static PagePrintOverride From(PrintOptions common) => new()
    {
        PaperSizeName = common.PaperSizeName, Orientation = common.Orientation, FitToPage = common.FitToPage, ScalePercent = common.ScalePercent,
    };

    /// <summary>The settings that apply to a page: its own if it has an override, otherwise the common ones.</summary>
    public static PagePrintOverride Effective(PrintOptions common, PagePrintOverride? own) => own ?? From(common);
}
