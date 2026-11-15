namespace CleanPrint.Core.Models;

public enum PageOrientation
{
    Portrait,
    Landscape,
}

public enum PageNumberPosition
{
    HeaderLeft,
    HeaderCenter,
    HeaderRight,
    FooterLeft,
    FooterCenter,
    FooterRight,
}

/// <summary>Full set of physical-printing controls: orientation, paper size, scale, fit-to-page,
/// margins, and optional page-number stamping. Passed to <see cref="Interfaces.IPrintingService"/>
/// so every tile in a job is printed with consistent settings.</summary>
public sealed class PrintOptions
{
    /// <summary>Must match a name from IPrintingService.GetAvailablePaperSizes for the selected
    /// printer. Null/empty means "use the printer's default paper size".</summary>
    public string? PaperSizeName { get; init; }

    public PageOrientation Orientation { get; init; } = PageOrientation.Portrait;

    /// <summary>100 = no additional scaling beyond whatever FitToPage computes (or 1:1 if
    /// FitToPage is false). Applied as a multiplier either way.</summary>
    public double ScalePercent { get; init; } = 100;

    /// <summary>When true, each tile is scaled to fit within the margin bounds (preserving aspect
    /// ratio) before ScalePercent is applied on top. When false, ScalePercent is the only scaling
    /// applied, relative to the tile's native pixel size — the print may overflow the page.</summary>
    public bool FitToPage { get; init; } = true;

    public double MarginLeftInches { get; init; } = 0.25;
    public double MarginTopInches { get; init; } = 0.25;
    public double MarginRightInches { get; init; } = 0.25;
    public double MarginBottomInches { get; init; } = 0.25;

    public bool ShowPageNumbers { get; init; }
    public PageNumberPosition PageNumberPosition { get; init; } = PageNumberPosition.FooterCenter;

    /// <summary>Number of copies of the whole job to print. Must be >= 1.</summary>
    public int Copies { get; init; } = 1;
}
