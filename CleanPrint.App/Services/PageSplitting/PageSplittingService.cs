using System;
using System.Collections.Generic;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services.PageSplitting;

/// <summary>
/// Splits a rendered page into Rows x Columns tiles. Each tile is expanded by the configured
/// overlap (clamped to the page bounds) so content near a cut line isn't lost.
/// </summary>
public sealed class PageSplittingService : IPageSplittingService
{
    public IReadOnlyList<SplitTile> Split(CapturedPage page, SplitSettings settings)
    {
        if (settings.Rows < 1 || settings.Columns < 1)
            throw new ArgumentException("Rows and Columns must be >= 1.");

        var tiles = new List<SplitTile>(settings.Rows * settings.Columns);

        // Cut boundaries via i * dimension / count (not a fixed "base size" multiplied out) so any
        // remainder from an uneven division spreads across cells (each cell differs by at most 1px)
        // instead of all landing in the last row/column — this is what makes the tiles genuinely
        // "equal parts" rather than equal-except-the-last-one.
        int[] columnBoundaries = ComputeBoundaries(page.Width, settings.Columns);
        int[] rowBoundaries = ComputeBoundaries(page.Height, settings.Rows);

        for (int row = 0; row < settings.Rows; row++)
        {
            for (int col = 0; col < settings.Columns; col++)
            {
                int cellX0 = columnBoundaries[col];
                int cellX1 = columnBoundaries[col + 1];
                int cellY0 = rowBoundaries[row];
                int cellY1 = rowBoundaries[row + 1];

                int x0 = Math.Max(0, cellX0 - settings.OverlapPixels);
                int y0 = Math.Max(0, cellY0 - settings.OverlapPixels);
                int x1 = Math.Min(page.Width, cellX1 + settings.OverlapPixels);
                int y1 = Math.Min(page.Height, cellY1 + settings.OverlapPixels);

                int tileWidth = x1 - x0;
                int tileHeight = y1 - y0;

                tiles.Add(new SplitTile
                {
                    RowIndex = row,
                    ColumnIndex = col,
                    Width = tileWidth,
                    Height = tileHeight,
                    Dpi = page.Dpi,
                    PixelData = ExtractRegion(page, x0, y0, tileWidth, tileHeight),
                });
            }
        }

        return tiles;
    }

    /// <summary>Returns count+1 boundary positions (0, ..., total) splitting [0, total) into
    /// `count` parts as evenly as possible — e.g. ComputeBoundaries(10, 3) = [0, 3, 7, 10]
    /// (sizes 3, 4, 3) rather than dumping all the remainder into one part.</summary>
    private static int[] ComputeBoundaries(int total, int count)
    {
        var boundaries = new int[count + 1];
        for (int i = 0; i <= count; i++)
            boundaries[i] = (int)((long)total * i / count);
        return boundaries;
    }

    private static byte[] ExtractRegion(CapturedPage page, int x0, int y0, int width, int height)
    {
        const int bytesPerPixel = 4;
        int srcStride = page.Width * bytesPerPixel;
        int dstStride = width * bytesPerPixel;
        var dest = new byte[dstStride * height];

        for (int row = 0; row < height; row++)
        {
            int srcOffset = (y0 + row) * srcStride + x0 * bytesPerPixel;
            int dstOffset = row * dstStride;
            Buffer.BlockCopy(page.PixelData, srcOffset, dest, dstOffset, dstStride);
        }

        return dest;
    }
}
