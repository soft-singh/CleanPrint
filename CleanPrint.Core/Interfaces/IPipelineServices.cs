using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Models;

namespace CleanPrint.Core.Interfaces;

/// <summary>Image enhancement operations: non-destructive effects, background whitening, and
/// auto-cropping to content.</summary>
public interface IImageProcessingService
{
    /// <summary>Applies every non-destructive effect in ImageEffectSettings, in a fixed pipeline
    /// order: brightness/contrast, midtone (gamma), background whitening (blended by strength),
    /// noise reduction, sharpen, then threshold (binarization) last, since it's the most drastic
    /// transform and any effect after it would have little visible impact on a pure black/white
    /// image. Replaces the old always-on, non-adjustable WhitenBackgroundAsync.</summary>
    Task<CapturedPage> ApplyEffectsAsync(CapturedPage page, ImageEffectSettings settings, CancellationToken ct = default);

    Task<CapturedPage> WhitenBackgroundAsync(CapturedPage page, CancellationToken ct = default);

    /// <summary>Detects the bounding box of non-white content and crops the page down to it (plus
    /// a padding margin), removing surrounding white space regardless of what page size was used
    /// when the source document was printed. whiteThreshold/paddingPixels default to the values
    /// this always used before they became adjustable via ImageEffectSettings.</summary>
    Task<CapturedPage> CropToContentAsync(CapturedPage page, int whiteThreshold = 250, int paddingPixels = -1, CancellationToken ct = default);

    /// <summary>Where CropToContentAsync would cut, without cutting: the rectangle of the page it keeps, or null if it would leave the page unchanged. The same decision, so
    /// the offset reported is exactly the one the crop takes — needed to know where a cropped page sits on the whole image.</summary>
    Task<PixelRect?> FindContentCropAsync(CapturedPage page, int whiteThreshold = 250, int paddingPixels = -1, CancellationToken ct = default);
}

/// <summary>Splits a page into a grid of overlapping tiles.</summary>
public interface IPageSplittingService
{
    IReadOnlyList<SplitTile> Split(CapturedPage page, SplitSettings settings);
}

/// <summary>Sends a set of split tiles to a physical printer as a single multi-page job.</summary>
public interface IPrintingService
{
    IReadOnlyList<string> GetInstalledPrinterNames();

    /// <summary>Paper sizes the given printer supports, with physical dimensions.</summary>
    IReadOnlyList<PaperSizeInfo> GetAvailablePaperSizes(string printerName);

    /// <summary>Prints all tiles as consecutive pages of one print job (one "Print" command),
    /// applying the given orientation/paper-size/scale/margins/page-numbering to every page.</summary>
    /// <param name="pageOverrides">Optional, one entry per tile: the paper size, orientation and scale that page uses INSTEAD of <paramref name="options"/>'s (null = the common ones).
    /// All of it is still ONE print job; the printer is told the page's own settings just before each page.</param>
    Task PrintTilesAsync(IReadOnlyList<SplitTile> tiles, string printerName, PrintOptions options, CancellationToken ct = default,
        IReadOnlyList<PagePrintOverride?>? pageOverrides = null);
}
