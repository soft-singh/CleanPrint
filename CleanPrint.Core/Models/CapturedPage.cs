namespace CleanPrint.Core.Models;

/// <summary>
/// A single page, rendered to a raw BGRA bitmap buffer. Kept format-agnostic (byte[] + dimensions)
/// so downstream stages don't depend on WPF or OpenCV types directly.
/// </summary>
public sealed class CapturedPage
{
    public required int PageNumber { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Dpi { get; init; }

    /// <summary>Raw pixel buffer, BGRA32, top-down, stride = Width * 4.</summary>
    public required byte[] PixelData { get; init; }

    public string SourceJobId { get; init; } = string.Empty;
}

/// <summary>Settings controlling how a page is split into a grid of output tiles.</summary>
public sealed class SplitSettings
{
    public int Rows { get; init; } = 1;
    public int Columns { get; init; } = 1;

    /// <summary>Overlap between adjacent tiles, in pixels, so content isn't lost at cut lines.</summary>
    public int OverlapPixels { get; init; } = 0;
}

/// <summary>A single output tile produced by page splitting.</summary>
public sealed class SplitTile
{
    public required int RowIndex { get; init; }
    public required int ColumnIndex { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] PixelData { get; init; }

    /// <summary>DPI of the source page this tile was cut from. Required to correctly convert
    /// pixel dimensions to physical size when printing or previewing — without this, a tile's raw
    /// pixel count gets mistaken for physical page units, producing wildly wrong scaling.</summary>
    public required int Dpi { get; init; }
}
