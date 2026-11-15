using System;
using System.Collections.Generic;

namespace CleanPrint.Core.Models;

/// <summary>One standard paper size, in millimetres, in PORTRAIT (width is the shorter side).</summary>
public sealed record BlankPaper(string Name, double WidthMm, double HeightMm)
{
    public override string ToString() => Name;
}

/// <summary>
/// The paper sizes offered by New Blank Job, and the arithmetic from a paper size + orientation + DPI to the page's size in pixels — kept here, free of
/// any UI, so it can be tested.
/// </summary>
public static class BlankPageSizes
{
    public const string CustomName = "Custom";

    public static readonly IReadOnlyList<BlankPaper> Standard = new[]
    {
        new BlankPaper("A3", 297, 420),
        new BlankPaper("A4", 210, 297),
        new BlankPaper("A5", 148, 210),
        new BlankPaper("A6", 105, 148),
        new BlankPaper("B5", 176, 250),
        new BlankPaper("Letter", 215.9, 279.4),     // 8.5 x 11 in
        new BlankPaper("Legal", 215.9, 355.6),      // 8.5 x 14 in
        new BlankPaper("Tabloid", 279.4, 431.8),    // 11 x 17 in
    };

    public const int MinDpi = 50;
    public const int MaxDpi = 1200;

    /// <summary>A page is held in memory as 4 bytes a pixel (and more than once: raw, cleaned, the preview), so there is a ceiling. 50 megapixels is
    /// A3 at 400 DPI — already a very large page.</summary>
    public const long MaxPixelsPerPage = 50_000_000;
    public const int MinSidePixels = 50;
    public const double MinSideMm = 10;
    public const double MaxSideMm = 2000;

    public const double MmPerInch = 25.4;

    public static int MmToPixels(double millimetres, int dpi) => (int)Math.Round(millimetres / MmPerInch * dpi);

    /// <summary>The page's pixel size. widthMm/heightMm are the PORTRAIT size (as stored in the catalogue); landscape swaps them.</summary>
    public static (int WidthPx, int HeightPx) PixelSize(double widthMm, double heightMm, bool landscape, int dpi)
    {
        double w = landscape ? heightMm : widthMm;
        double h = landscape ? widthMm : heightMm;
        return (MmToPixels(w, dpi), MmToPixels(h, dpi));
    }

    /// <summary>Null if a page of this size can be made; otherwise a sentence saying what to change.</summary>
    public static string? Validate(double widthMm, double heightMm, int dpi)
    {
        if (double.IsNaN(widthMm) || double.IsNaN(heightMm) || widthMm < MinSideMm || heightMm < MinSideMm)
            return $"Each side must be at least {MinSideMm:0} mm ({MinSideMm / MmPerInch:0.0#} in).";
        if (widthMm > MaxSideMm || heightMm > MaxSideMm)
            return $"Each side can be at most {MaxSideMm:0} mm ({MaxSideMm / MmPerInch:0} in).";
        if (dpi < MinDpi || dpi > MaxDpi)
            return $"Resolution must be between {MinDpi} and {MaxDpi} DPI.";

        var (w, h) = PixelSize(widthMm, heightMm, false, dpi);
        if (w < MinSidePixels || h < MinSidePixels)
            return "That would be too few pixels to be a page — raise the resolution or the size.";
        if ((long)w * h > MaxPixelsPerPage)
            return $"That page would be {w:N0} × {h:N0} pixels ({(long)w * h / 1_000_000.0:0} megapixels) — more than the {MaxPixelsPerPage / 1_000_000} megapixel limit. Lower the resolution or the size.";
        return null;
    }
}
