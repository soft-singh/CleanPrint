using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Models;
using Microsoft.Extensions.Logging;

namespace CleanPrint.App.Services.Printing;

/// <summary>
/// Prints a list of <see cref="SplitTile"/>s to a physical printer using
/// <see cref="System.Drawing.Printing.PrintDocument"/>. All tiles are queued as pages of a single
/// job, so one call = one entry in the print queue, printed with one "Print" command.
/// </summary>
public sealed class PrintingService : IPrintingService
{
    private readonly ILogger<PrintingService> _logger;

    public PrintingService(ILogger<PrintingService> logger) => _logger = logger;

    public IReadOnlyList<string> GetInstalledPrinterNames()
    {
        var names = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            names.Add(name);
        return names;
    }

    public IReadOnlyList<PaperSizeInfo> GetAvailablePaperSizes(string printerName)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printerName };
            if (!settings.IsValid) return Array.Empty<PaperSizeInfo>();

            var sizes = new List<PaperSizeInfo>();
            foreach (PaperSize size in settings.PaperSizes)
            {
                // PaperSize.Width/Height are already in hundredths of an inch.
                sizes.Add(new PaperSizeInfo
                {
                    Name = size.PaperName,
                    WidthInches = size.Width / 100.0,
                    HeightInches = size.Height / 100.0,
                });
            }
            return sizes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate paper sizes for '{Printer}'.", printerName);
            return Array.Empty<PaperSizeInfo>();
        }
    }

    public Task PrintTilesAsync(IReadOnlyList<SplitTile> tiles, string printerName, PrintOptions options, CancellationToken ct = default,
        IReadOnlyList<PagePrintOverride?>? pageOverrides = null)
    {
        if (tiles.Count == 0)
            throw new ArgumentException("No tiles to print. Apply a split first.", nameof(tiles));
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("No printer selected.", nameof(printerName));

        return Task.Run(() =>
        {
            using var document = new PrintDocument
            {
                DocumentName = $"Clean Print - {tiles.Count} page(s)",
                PrinterSettings = { PrinterName = printerName },
            };

            if (!document.PrinterSettings.IsValid)
                throw new InvalidOperationException($"Printer '{printerName}' is not available.");

            // Let the print driver handle multiple copies itself (most support it natively and do
            // a better job than us re-submitting the same pages N times) — clamp defensively since
            // PrinterSettings.Copies is a signed 16-bit count and 0 or negative isn't meaningful.
            document.PrinterSettings.Copies = (short)Math.Clamp(options.Copies, 1, short.MaxValue);

            document.DefaultPageSettings.Landscape = options.Orientation == PageOrientation.Landscape;
            document.DefaultPageSettings.Margins = new Margins(
                InchesToHundredths(options.MarginLeftInches),
                InchesToHundredths(options.MarginRightInches),
                InchesToHundredths(options.MarginTopInches),
                InchesToHundredths(options.MarginBottomInches));

            PaperSize? FindPaper(string? name) => string.IsNullOrWhiteSpace(name)
                ? null
                : document.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => string.Equals(p.PaperName, name, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(options.PaperSizeName))
            {
                var match = FindPaper(options.PaperSizeName);
                if (match is not null)
                    document.DefaultPageSettings.PaperSize = match;
                else
                    _logger.LogWarning("Paper size '{Size}' not found on '{Printer}'; using printer default.", options.PaperSizeName, printerName);
            }

            int pageIndex = 0;

            // The settings one page prints with: its own, if it was set apart (see PagePrintOverride), otherwise the job's.
            PagePrintOverride SettingsFor(int index) =>
                PagePrintOverride.Effective(options, pageOverrides is not null && index < pageOverrides.Count ? pageOverrides[index] : null);

            // Raised before EACH page, with that page's own PageSettings — which is how one job can mix paper sizes and orientations. Only a page that was set apart differs
            // from the defaults above, but setting it for every page keeps the rule in one place. (Margins are common to the job and already in the defaults.)
            document.QueryPageSettings += (_, e) =>
            {
                var settings = SettingsFor(pageIndex);
                e.PageSettings.Landscape = settings.Orientation == PageOrientation.Landscape;
                if (FindPaper(settings.PaperSizeName) is { } paper)
                    e.PageSettings.PaperSize = paper;
                else if (!string.IsNullOrWhiteSpace(settings.PaperSizeName))
                    _logger.LogWarning("Paper size '{Size}' for page {Page} not found on '{Printer}'; using the job's.", settings.PaperSizeName, pageIndex + 1, printerName);
            };

            document.PrintPage += (_, e) =>
            {
                ct.ThrowIfCancellationRequested();

                var tile = tiles[pageIndex];
                var pageSettings = SettingsFor(pageIndex);
                using var bitmap = TileToBitmap(tile);

                // e.MarginBounds only reflects the USER-SPECIFIED software margin
                // (options.MarginXInches) — it does NOT account for the printer's own
                // hardware-imposed minimum printable area (PageSettings.HardMarginX/Y). If the
                // software margin is smaller than what the printer can physically reach, MarginBounds
                // overstates the true printable area, and content drawn near its edge gets silently
                // clipped by the printer/driver itself — not by anything in our own layout math. This
                // is what "fit to page + a small margin still trims the image" almost always is:
                // clamp each edge to whichever is more restrictive, the software margin (already
                // baked into MarginBounds) or the printer's own hardware minimum, so our own "fit"
                // calculation shrinks the image to what the printer can actually deliver instead of
                // assuming more room than physically exists.
                var pageBounds = e.PageBounds; // full page, already orientation-adjusted
                int hardMarginX = (int)Math.Round(e.PageSettings!.HardMarginX);
                int hardMarginY = (int)Math.Round(e.PageSettings.HardMarginY);

                var marginBounds = e.MarginBounds;
                int left = Math.Max(marginBounds.Left, pageBounds.Left + hardMarginX);
                int top = Math.Max(marginBounds.Top, pageBounds.Top + hardMarginY);
                int right = Math.Min(marginBounds.Right, pageBounds.Right - hardMarginX);
                int bottom = Math.Min(marginBounds.Bottom, pageBounds.Bottom - hardMarginY);
                var bounds = Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));

                var rect = PrintLayoutMath.ComputeDrawRect(
                    availableWidthInches: bounds.Width / 100.0,
                    availableHeightInches: bounds.Height / 100.0,
                    tileWidthPixels: tile.Width,
                    tileHeightPixels: tile.Height,
                    tileDpi: tile.Dpi,
                    fitToPage: pageSettings.FitToPage,
                    scalePercent: pageSettings.ScalePercent);

                int x = bounds.X + (int)Math.Round(rect.XInches * 100);
                int y = bounds.Y + (int)Math.Round(rect.YInches * 100);
                int drawWidth = Math.Max(1, (int)Math.Round(rect.WidthInches * 100));
                int drawHeight = Math.Max(1, (int)Math.Round(rect.HeightInches * 100));

                e.Graphics!.DrawImage(bitmap, x, y, drawWidth, drawHeight);

                if (options.ShowPageNumbers)
                    DrawPageNumber(e, pageIndex + 1, tiles.Count, options.PageNumberPosition);

                pageIndex++;
                e.HasMorePages = pageIndex < tiles.Count;
            };

            _logger.LogInformation("Printing {Count} page(s) to '{Printer}' as a single job ({Orientation}, {Paper}, {Scale}%, fit={Fit}).",
                tiles.Count, printerName, options.Orientation, options.PaperSizeName ?? "default", options.ScalePercent, options.FitToPage);
            document.Print();
            _logger.LogInformation("Print job submitted: {Job}", document.DocumentName);
        }, ct);
    }

    private static void DrawPageNumber(PrintPageEventArgs e, int pageNumber, int totalPages, PageNumberPosition position)
    {
        string text = $"Page {pageNumber} of {totalPages}";
        using var font = new Font("Arial", 9);
        var textSize = e.Graphics!.MeasureString(text, font);

        bool isHeader = position is PageNumberPosition.HeaderLeft or PageNumberPosition.HeaderCenter or PageNumberPosition.HeaderRight;
        float y = isHeader
            ? e.PageBounds.Top + 10
            : e.PageBounds.Bottom - textSize.Height - 10;

        float x = position switch
        {
            PageNumberPosition.HeaderLeft or PageNumberPosition.FooterLeft => e.PageBounds.Left + 20,
            PageNumberPosition.HeaderRight or PageNumberPosition.FooterRight => e.PageBounds.Right - textSize.Width - 20,
            _ => e.PageBounds.Left + (e.PageBounds.Width - textSize.Width) / 2,
        };

        e.Graphics.DrawString(text, font, Brushes.Black, x, y);
    }

    private static int InchesToHundredths(double inches) => Math.Max(0, (int)Math.Round(inches * 100));

    private static Bitmap TileToBitmap(SplitTile tile)
    {
        var bitmap = new Bitmap(tile.Width, tile.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, tile.Width, tile.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            Marshal.Copy(tile.PixelData, 0, data.Scan0, tile.PixelData.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
