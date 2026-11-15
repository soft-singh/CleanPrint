namespace CleanPrint.Core.Models;

/// <summary>A printer-supported paper size, with physical dimensions in inches (portrait
/// orientation — callers swap width/height themselves for landscape).</summary>
public sealed class PaperSizeInfo
{
    public required string Name { get; init; }
    public required double WidthInches { get; init; }
    public required double HeightInches { get; init; }

    public override string ToString() => Name;
}
