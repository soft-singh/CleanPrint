using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanPrint.Core.Models;

/// <summary>Which pages of a job a print covers: all of them, just one, or the ones typed into the range box.</summary>
public enum PrintRangeKind
{
    All,
    Current,
    Selected,
}

/// <summary>
/// Which pages a whole-page print or export covers, and how the Print dialog's preview moves through them. Pure and free of any UI so it can be tested.
/// Page INDICES here are 0-based positions in the job; what a person types and sees is 1-based.
/// </summary>
public static class PrintPageRange
{
    /// <summary>
    /// Parses a range like "1-3,5,8-10" (1-based, as typed) into sorted, de-duplicated 0-based indices inside the job. Entries that are out of range or aren't numbers are skipped
    /// rather than reported — a range box is exactly where a stray typo gets left in place. A reversed range ("5-2") counts as 2-5.
    /// </summary>
    public static List<int> Parse(string? rangeText, int pageCount)
    {
        var result = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(rangeText)) return new List<int>();

        foreach (var part in rangeText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (bounds.Length == 2 && int.TryParse(bounds[0], out int start) && int.TryParse(bounds[1], out int end))
            {
                for (int page = Math.Min(start, end); page <= Math.Max(start, end); page++)
                    if (page >= 1 && page <= pageCount) result.Add(page - 1);
            }
            else if (bounds.Length == 1 && int.TryParse(bounds[0], out int single) && single >= 1 && single <= pageCount)
            {
                result.Add(single - 1);
            }
        }
        return result.ToList();
    }

    /// <summary>The pages a print covers, as 0-based indices in job order. <paramref name="currentPageIndex"/> is the page "Current Page" means.</summary>
    public static List<int> Resolve(PrintRangeKind kind, int currentPageIndex, string? rangeText, int pageCount) => kind switch
    {
        _ when pageCount <= 0 => new List<int>(),
        PrintRangeKind.Current => new List<int> { Math.Clamp(currentPageIndex, 0, pageCount - 1) },
        PrintRangeKind.Selected => Parse(rangeText, pageCount),
        _ => Enumerable.Range(0, pageCount).ToList(),
    };

    // ---- moving through the pages that are on show

    /// <summary>The page to show after the set of visible pages changed: the same page if it is still in the set, otherwise the first of the set; -1 if the set is empty.</summary>
    public static int KeepOrFirst(IReadOnlyList<int> visible, int currentPageIndex) =>
        visible.Count == 0 ? -1 : visible.Contains(currentPageIndex) ? currentPageIndex : visible[0];

    /// <summary>1-based position of a page among the visible ones, or 0 if it isn't one of them.</summary>
    public static int Position(IReadOnlyList<int> visible, int pageIndex)
    {
        for (int i = 0; i < visible.Count; i++)
            if (visible[i] == pageIndex) return i + 1;
        return 0;
    }

    /// <summary>The visible page at a 1-based position, clamped into the set (so a typo in the page box lands somewhere sensible); -1 if the set is empty.</summary>
    public static int AtPosition(IReadOnlyList<int> visible, int position) =>
        visible.Count == 0 ? -1 : visible[Math.Clamp(position, 1, visible.Count) - 1];

    /// <summary>The visible page <paramref name="delta"/> places after (or before, if negative) the given one; stays put at either end.</summary>
    public static int Step(IReadOnlyList<int> visible, int currentPageIndex, int delta)
    {
        if (visible.Count == 0) return -1;
        int position = Position(visible, currentPageIndex);
        if (position == 0) return visible[0];
        return visible[Math.Clamp(position - 1 + delta, 0, visible.Count - 1)];
    }
}
