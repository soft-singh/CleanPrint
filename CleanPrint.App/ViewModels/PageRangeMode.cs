namespace CleanPrint.App.ViewModels;

/// <summary>Which pages to include when printing/exporting in whole-page (Size tab) mode — see
/// MainViewModel.PageRangeMode and ResolvePageIndicesToPrint. Doesn't apply to Split mode, which
/// already has its own distinct workflow (Apply Split (This Page) vs Process All Pages) that
/// implicitly covers "just this page" vs "every page".</summary>
public enum PageRangeMode
{
    All,
    Current,
    Selected,
}
