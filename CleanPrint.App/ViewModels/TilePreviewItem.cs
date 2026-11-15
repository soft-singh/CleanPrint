using System.Windows.Media.Imaging;

namespace CleanPrint.App.ViewModels;

/// <summary>A single split-page thumbnail with a human-readable position label, for the preview gallery.</summary>
public sealed class TilePreviewItem
{
    public required BitmapSource Image { get; init; }
    public required string Label { get; init; }
}
