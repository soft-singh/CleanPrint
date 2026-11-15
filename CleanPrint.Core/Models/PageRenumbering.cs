using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanPrint.Core.Models;

/// <summary>
/// What happens to page numbers (1-based) when a page is inserted into a job or deleted from it. Pages are files named page_0001.png … in order, and
/// several things are keyed by page number — the annotations, the per-page split overrides, the undo history — so inserting or deleting a page must
/// move every one of them to the same new number the file gets. These are the rules, kept free of files and UI so they can be tested.
/// </summary>
public static class PageRenumbering
{
    public static string FileName(int pageNumber, string extension = ".png") => $"page_{pageNumber:D4}{extension}";

    /// <summary>A page's new number after a NEW page is inserted AT <paramref name="insertedPageNumber"/>: that page and everything after it move up one.</summary>
    public static int AfterInsert(int pageNumber, int insertedPageNumber) =>
        pageNumber >= insertedPageNumber ? pageNumber + 1 : pageNumber;

    /// <summary>A page's new number after page <paramref name="deletedPageNumber"/> is deleted: null for the deleted page itself, everything after it moves down one.</summary>
    public static int? AfterDelete(int pageNumber, int deletedPageNumber) =>
        pageNumber == deletedPageNumber ? null
        : pageNumber > deletedPageNumber ? pageNumber - 1
        : pageNumber;

    /// <summary>Re-keys a dictionary by page number. Entries whose page maps to null (the deleted page) are dropped.</summary>
    public static Dictionary<int, T> Remap<T>(IReadOnlyDictionary<int, T> source, Func<int, int?> map)
    {
        var result = new Dictionary<int, T>();
        foreach (var (key, value) in source)
            if (map(key) is int newKey) result[newKey] = value;
        return result;
    }

    /// <summary>
    /// The file renames that make room for a page inserted at <paramref name="insertedPageNumber"/>, in an order that never overwrites a file that hasn't moved
    /// yet: the LAST page moves first (up into the free slot at the end), then the one before it, and so on down. Pages before the insertion point stay put.
    /// </summary>
    public static List<(int From, int To)> PlanInsertRenames(int pageCount, int insertedPageNumber)
    {
        var plan = new List<(int, int)>();
        for (int page = pageCount; page >= insertedPageNumber; page--)
            plan.Add((page, page + 1));
        return plan;
    }

    /// <summary>
    /// The file renames that close the gap after page <paramref name="deletedPageNumber"/> was removed, in a collision-safe order: the page right after the gap moves
    /// first, then the next, and so on up.
    /// </summary>
    public static List<(int From, int To)> PlanDeleteRenames(int pageCount, int deletedPageNumber)
    {
        var plan = new List<(int, int)>();
        for (int page = deletedPageNumber + 1; page <= pageCount; page++)
            plan.Add((page, page - 1));
        return plan;
    }

    /// <summary>The page to show after deleting <paramref name="deletedIndex"/> (0-based) from a job that had <paramref name="oldPageCount"/> pages: the page that took its place,
    /// or the new last page if the last one was deleted.</summary>
    public static int IndexToShowAfterDelete(int deletedIndex, int oldPageCount) =>
        Math.Min(deletedIndex, Math.Max(0, oldPageCount - 2));
}
