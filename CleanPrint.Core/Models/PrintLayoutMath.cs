using System;

namespace CleanPrint.Core.Models;

/// <summary>
/// Computes where a tile lands within the available (post-margin) area of a page, given fit/scale
/// settings. This is deliberately the ONE place this math lives — both the physical printer output
/// (PrintingService, in GDI+ hundredths-of-an-inch units) and the on-screen split-preview gallery
/// (in WPF preview pixels) call through here, so what you see in the gallery is guaranteed to match
/// what actually prints.
/// </summary>
public static class PrintLayoutMath
{
    public readonly record struct DrawRect(double XInches, double YInches, double WidthInches, double HeightInches);

    /// <summary>
    /// Computes the tile's draw rectangle in inches, relative to the top-left of the available
    /// (already margin-subtracted) area.
    /// </summary>
    /// <param name="availableWidthInches">Page width minus left/right margins.</param>
    /// <param name="availableHeightInches">Page height minus top/bottom margins.</param>
    /// <param name="tileWidthPixels">Tile's raw pixel width.</param>
    /// <param name="tileHeightPixels">Tile's raw pixel height.</param>
    /// <param name="tileDpi">DPI the tile's pixels were captured at — required to convert pixel
    /// count to physical size. Without this, pixel count gets mistaken for physical units.</param>
    /// <param name="fitToPage">If true, the tile is scaled (preserving aspect ratio) to fill as
    /// much of the available area as possible without overflowing either dimension, and
    /// scalePercent is ignored entirely — the two are mutually exclusive modes (the UI now
    /// enforces this with radio buttons), not a base scale with an additional multiplier on top.
    /// Multiplying them together previously meant a stale scalePercent value left over from a
    /// prior "custom scale" session could silently affect Fit to Page's output too, which is
    /// exactly the kind of "why is this trimmed even at Fit + 100%" confusion this eliminates at
    /// the math level, not just the UI level.
    ///
    /// The computed fit scale is deliberately shrunk by an extra FitToPageSafetyFactor (1%) beyond
    /// the mathematically "perfect" fit — a print driver's actual printable area can differ very
    /// slightly from what PrintingService computes (rounding when converting between units, a
    /// printer reporting its hardware margin slightly differently than expected, etc.), and a
    /// "perfect" fit leaves zero tolerance for any of that: the tiniest discrepancy in either
    /// direction means the image is drawn a hair outside the printable area and the printer clips
    /// it right at that edge. Shrinking slightly means there's always a small buffer, so a few
    /// hundredths of an inch of real-world imprecision can't cause visible trimming — this trades
    /// an imperceptible amount of size for guaranteed margin safety, matching "shrink it according
    /// to the margin specified, don't trim it" rather than trying to land exactly on the edge.
    ///
    /// If fitToPage is false, the tile is drawn at its true physical size (pixels / dpi) times
    /// scalePercent — it may overflow the page, which is intentional when this is off.</param>
    /// <param name="scalePercent">Only used when fitToPage is false. 100 = true physical size, no
    /// extra scaling.</param>
    public static DrawRect ComputeDrawRect(
        double availableWidthInches, double availableHeightInches,
        int tileWidthPixels, int tileHeightPixels, int tileDpi,
        bool fitToPage, double scalePercent)
    {
        const double FitToPageSafetyFactor = 0.99; // 1% deliberate shrink — see doc comment above

        double dpi = tileDpi > 0 ? tileDpi : 96;
        double tileWidthInches = tileWidthPixels / dpi;
        double tileHeightInches = tileHeightPixels / dpi;

        double scale = fitToPage
            ? Math.Min(availableWidthInches / tileWidthInches, availableHeightInches / tileHeightInches) * FitToPageSafetyFactor
            : scalePercent / 100.0;

        double drawWidth = tileWidthInches * scale;
        double drawHeight = tileHeightInches * scale;
        double drawX = (availableWidthInches - drawWidth) / 2;
        double drawY = (availableHeightInches - drawHeight) / 2;

        return new DrawRect(drawX, drawY, drawWidth, drawHeight);
    }
}
