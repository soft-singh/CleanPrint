using System;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace CleanPrint.App.Services.ImageProcessing;

/// <summary>
/// Flattens uneven/yellowed scan backgrounds to clean white while preserving dark text/marks AND
/// color content. Approach: independently estimate the local background of each of the Blue/
/// Green/Red channels via a large-kernel morphological close, divide each channel by its OWN
/// background estimate, then normalize — the same "illumination correction" technique commonly
/// applied to a single grayscale channel, just run once per color channel instead of collapsing to
/// grayscale first. Converting to grayscale before correcting (an earlier version of this method
/// did exactly that) discards all color information permanently — the corrected output ends up
/// black-and-white regardless of how colorful the original scan was, since every channel gets
/// forced to the same value. Applying the same math per-channel instead means each channel is
/// corrected relative to its own local tone, so a red mark on a discolored background comes out
/// whitened AND still recognizably red, not converted to gray.
///
/// Caveat: this correction is tuned for the common case here — a fairly uniform paper background
/// with foreground text/line-art. A large solid-colored region or photo bigger than the background-
/// estimation kernel can show some hue drift, since the morphological "background" estimate for
/// each channel would be dominated by that region's own color rather than the true paper tone
/// within it. Not expected to matter for typical scanned documents, but worth knowing about.
/// </summary>
public sealed class BackgroundWhiteningService : IImageProcessingService
{
    private readonly ILogger<BackgroundWhiteningService> _logger;

    public BackgroundWhiteningService(ILogger<BackgroundWhiteningService> logger) => _logger = logger;

    public Task<CapturedPage> WhitenBackgroundAsync(CapturedPage page, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var src = BytesToMat(page.PixelData, page.Width, page.Height);
            using var result = ApplyWhiteningCore(src, page.Dpi);
            var outBytes = MatToBytes(result);

            _logger.LogDebug("Background whitening applied to page {Page} (per-channel color-preserving).", page.PageNumber);

            return new CapturedPage
            {
                PageNumber = page.PageNumber,
                Width = page.Width,
                Height = page.Height,
                Dpi = page.Dpi,
                PixelData = outBytes,
                SourceJobId = page.SourceJobId,
            };
        }, ct);
    }

    /// <summary>The actual per-channel illumination-correction math, extracted so both the
    /// standalone WhitenBackgroundAsync (kept for any caller still using it directly) and
    /// ApplyEffectsAsync (the new pipeline, which needs the RESULT as a Mat to blend by strength
    /// rather than already-encoded bytes) share the exact same implementation. Caller owns and
    /// must dispose the returned Mat.</summary>
    private static Mat ApplyWhiteningCore(Mat src, int dpi)
    {
        // [Blue, Green, Red, Alpha] — Alpha (index 3) is left completely untouched below; scanned
        // pages are opaque, and illumination correction has no meaningful concept of "correcting"
        // transparency anyway.
        Mat[] channels = Cv2.Split(src);

        int kernelSize = Math.Max(15, (int)(dpi / 40.0)) | 1; // odd, scales with DPI
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));

        try
        {
            for (int i = 0; i < 3; i++) // B, G, R only
            {
                var original = channels[i];

                using var background = new Mat();
                Cv2.MorphologyEx(original, background, MorphTypes.Close, kernel);

                using var channel32 = new Mat();
                using var bg32 = new Mat();
                original.ConvertTo(channel32, MatType.CV_32F);
                background.ConvertTo(bg32, MatType.CV_32F, 1.0, 1.0); // +1 avoids div-by-zero

                using var divided = new Mat();
                Cv2.Divide(channel32, bg32, divided, 255.0);

                var corrected = new Mat();
                divided.ConvertTo(corrected, MatType.CV_8U);

                original.Dispose(); // replaced by 'corrected' below — done with the original channel now
                channels[i] = corrected;
            }

            var result = new Mat();
            Cv2.Merge(channels, result);
            return result;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static Mat BytesToMat(byte[] bgra, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
        return mat;
    }

    /// <summary>
    /// Runs every enabled effect in ImageEffectSettings over the page in one pass, in a fixed
    /// order (see the interface doc comment for why this specific order). Each stage takes
    /// ownership of the previous stage's Mat and disposes it once done, so only ever one
    /// intermediate Mat is alive at a time regardless of how many effects are enabled.
    /// </summary>
    public Task<CapturedPage> ApplyEffectsAsync(CapturedPage page, ImageEffectSettings settings, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Mat current = BytesToMat(page.PixelData, page.Width, page.Height);

            void Replace(Mat next)
            {
                current.Dispose();
                current = next;
            }

            // 1. Brightness/Contrast — a standard alpha (gain)/beta (offset) linear transform.
            if (settings.Brightness != 0 || settings.Contrast != 0)
            {
                var adjusted = new Mat();
                double alpha = 1.0 + Math.Clamp(settings.Contrast, -100, 100) / 100.0;
                double beta = Math.Clamp(settings.Brightness, -100, 100) * 2.55; // map -100..100 onto roughly -255..255
                current.ConvertTo(adjusted, -1, alpha, beta);
                Replace(adjusted);
            }

            // 2. Midtone (gamma) — a lookup table so it's a single pass regardless of image size.
            if (Math.Abs(settings.Midtone - 1.0) > 0.001)
            {
                double gamma = Math.Clamp(settings.Midtone, 0.1, 3.0);
                var lutData = new byte[256];
                for (int i = 0; i < 256; i++)
                    lutData[i] = (byte)Math.Clamp(Math.Pow(i / 255.0, 1.0 / gamma) * 255.0, 0, 255);
                using var lut = new Mat(1, 256, MatType.CV_8UC1);
                System.Runtime.InteropServices.Marshal.Copy(lutData, 0, lut.Data, 256);

                var gammaCorrected = new Mat();
                Cv2.LUT(current, lut, gammaCorrected);
                Replace(gammaCorrected);
            }

            // 3. Background whitening, blended by strength (100 = the original full-strength
            // correction this app always applied before this was adjustable).
            if (settings.WhiteningEnabled && settings.WhiteningStrength > 0)
            {
                using var whitened = ApplyWhiteningCore(current, page.Dpi);
                double strength = Math.Clamp(settings.WhiteningStrength, 0, 100) / 100.0;
                if (strength >= 0.999)
                {
                    Replace(whitened.Clone());
                }
                else
                {
                    var blended = new Mat();
                    Cv2.AddWeighted(whitened, strength, current, 1 - strength, 0, blended);
                    Replace(blended);
                }
            }

            // 4. Noise reduction — median blur; kernel size grows with strength.
            if (settings.NoiseReduction > 0)
            {
                int kSize = (1 + 2 * (int)Math.Round(Math.Clamp(settings.NoiseReduction, 0, 100) / 100.0 * 4)); // 1,3,5,7,9
                if (kSize > 1)
                {
                    var denoised = new Mat();
                    Cv2.MedianBlur(current, denoised, kSize);
                    Replace(denoised);
                }
            }

            // 5. Sharpen — unsharp mask: blur a copy, then push the original away from that blur.
            if (settings.Sharpen > 0)
            {
                double amount = Math.Clamp(settings.Sharpen, 0, 100) / 100.0 * 1.5; // 0..1.5
                using var blurred = new Mat();
                Cv2.GaussianBlur(current, blurred, new Size(0, 0), 3);
                var sharpened = new Mat();
                Cv2.AddWeighted(current, 1 + amount, blurred, -amount, 0, sharpened);
                Replace(sharpened);
            }

            // 6. Threshold (binarize) — last, since it's the most drastic transform; anything
            // after it would have little visible effect on a pure black/white image.
            if (settings.ThresholdEnabled)
            {
                using var gray = new Mat();
                Cv2.CvtColor(current, gray, ColorConversionCodes.BGRA2GRAY);
                using var binary = new Mat();
                Cv2.Threshold(gray, binary, Math.Clamp(settings.ThresholdLevel, 0, 255), 255, ThresholdTypes.Binary);
                var binaryBgra = new Mat();
                Cv2.CvtColor(binary, binaryBgra, ColorConversionCodes.GRAY2BGRA);
                Replace(binaryBgra);
            }

            // Defensive: brightness/contrast's ConvertTo and sharpen's AddWeighted both apply
            // their transform across all 4 channels including alpha (there's no way to tell them
            // "skip channel 3") — scanned pages are always fully opaque, so force alpha back to
            // 255 here regardless of what any combination of effects did to it, rather than risk
            // introducing unintended transparency into the output.
            if (settings.Brightness != 0 || settings.Contrast != 0 || settings.Sharpen > 0)
            {
                Mat[] finalChannels = Cv2.Split(current);
                finalChannels[3].SetTo(new Scalar(255));
                var withFixedAlpha = new Mat();
                Cv2.Merge(finalChannels, withFixedAlpha);
                foreach (var ch in finalChannels) ch.Dispose();
                Replace(withFixedAlpha);
            }

            var outBytes = MatToBytes(current);
            current.Dispose();

            return new CapturedPage
            {
                PageNumber = page.PageNumber,
                Width = page.Width,
                Height = page.Height,
                Dpi = page.Dpi,
                PixelData = outBytes,
                SourceJobId = page.SourceJobId,
            };
        }, ct);
    }

    /// <summary>
    /// Detects the bounding box of non-white content and crops the page down to it (plus a
    /// padding margin). This is what lets the split/print pipeline work on the document's actual
    /// content regardless of the page size a source app used when printing — no custom printer
    /// driver required, since Clean Print's virtual printer piggybacks on the system's built-in
    /// XPS driver (see project README) and can't inject a custom "actual size" paper option into
    /// other apps' print dialogs.
    /// </summary>
    public Task<CapturedPage> CropToContentAsync(CapturedPage page, int whiteThreshold = 250, int paddingPixels = -1, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var src = BytesToMat(page.PixelData, page.Width, page.Height);
            if (ComputeContentCrop(src, page, whiteThreshold, paddingPixels) is not { } crop)
                return page; // nothing to crop (blank, or the content already fills the page) — returned unchanged rather than produce a 0x0 image

            using var roi = new Mat(src, crop);
            using var contiguousRoi = roi.Clone(); // ROI views are non-contiguous (row gaps to the parent's full width); Clone() makes a packed copy MatToBytes can safely read
            var croppedBytes = MatToBytes(contiguousRoi);

            return new CapturedPage
            {
                PageNumber = page.PageNumber,
                Width = crop.Width,
                Height = crop.Height,
                Dpi = page.Dpi,
                PixelData = croppedBytes,
                SourceJobId = page.SourceJobId,
            };
        }, ct);
    }

    /// <summary>Where CropToContentAsync would cut — the same decision, without cutting. Null when it would leave the page as it is. Lets a caller know how far the
    /// cropped page is offset from the whole image (annotations are measured from the cropped page's corner).</summary>
    public Task<PixelRect?> FindContentCropAsync(CapturedPage page, int whiteThreshold = 250, int paddingPixels = -1, CancellationToken ct = default)
    {
        return Task.Run<PixelRect?>(() =>
        {
            using var src = BytesToMat(page.PixelData, page.Width, page.Height);
            return ComputeContentCrop(src, page, whiteThreshold, paddingPixels) is { } crop ? new PixelRect(crop.X, crop.Y, crop.Width, crop.Height) : null;
        }, ct);
    }

    /// <summary>The rectangle of the page to keep when cropping to content: the bounding box of everything darker than the threshold, plus padding. Null if the page is
    /// blank or the box already covers the whole page.</summary>
    private Rect? ComputeContentCrop(Mat src, CapturedPage page, int whiteThreshold, int paddingPixels)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);

        // Threshold: anything meaningfully darker than white counts as "content". Defaults to
        // 250 (out of 255) rather than 254/255 to tolerate faint scanner noise on an otherwise
        // blank page without treating it as content and cropping nothing — adjustable via
        // ImageEffectSettings.AutoCropWhiteThreshold for scans where this default is too
        // aggressive/lenient.
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, thresh: Math.Clamp(whiteThreshold, 0, 255), maxval: 255, type: ThresholdTypes.BinaryInv);

        // Collapse the mask to "does this column contain any content" / "does this row" via
        // Reduce, then scan those small 1D arrays — far cheaper than enumerating every
        // non-zero pixel individually on a multi-million-pixel 600 DPI scan.
        using var colPresence = new Mat(); // 1 x width
        Cv2.Reduce(mask, colPresence, ReduceDimension.Row, ReduceTypes.Max, -1);
        using var rowPresence = new Mat(); // height x 1
        Cv2.Reduce(mask, rowPresence, ReduceDimension.Column, ReduceTypes.Max, -1);

        var colBytes = new byte[page.Width];
        System.Runtime.InteropServices.Marshal.Copy(colPresence.Data, colBytes, 0, page.Width);
        var rowBytes = new byte[page.Height];
        System.Runtime.InteropServices.Marshal.Copy(rowPresence.Data, rowBytes, 0, page.Height);

        int minX = Array.FindIndex(colBytes, b => b > 0);
        int maxX = Array.FindLastIndex(colBytes, b => b > 0);
        int minY = Array.FindIndex(rowBytes, b => b > 0);
        int maxY = Array.FindLastIndex(rowBytes, b => b > 0);

        if (minX < 0 || minY < 0)
        {
            _logger.LogDebug("Page {Page} appears blank; skipping crop.", page.PageNumber);
            return null;
        }

        var boundingRect = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);

        // paddingPixels < 0 means "use the built-in DPI-scaled default", matching what this
        // always used before it became adjustable via ImageEffectSettings.AutoCropPaddingPixels.
        int paddingPx = paddingPixels >= 0 ? paddingPixels : Math.Max(10, page.Dpi / 60);
        int x0 = Math.Max(0, boundingRect.X - paddingPx);
        int y0 = Math.Max(0, boundingRect.Y - paddingPx);
        int x1 = Math.Min(page.Width, boundingRect.X + boundingRect.Width + paddingPx);
        int y1 = Math.Min(page.Height, boundingRect.Y + boundingRect.Height + paddingPx);

        int croppedWidth = x1 - x0;
        int croppedHeight = y1 - y0;

        if (croppedWidth <= 0 || croppedHeight <= 0 || (croppedWidth == page.Width && croppedHeight == page.Height))
        {
            _logger.LogDebug("Page {Page} crop bounds equal the original size; nothing to trim.", page.PageNumber);
            return null;
        }

        _logger.LogDebug("Cropped page {Page} from {OrigW}x{OrigH} to {NewW}x{NewH} (content bounds + {Pad}px padding).",
            page.PageNumber, page.Width, page.Height, croppedWidth, croppedHeight, paddingPx);
        return new Rect(x0, y0, croppedWidth, croppedHeight);
    }

    private static byte[] MatToBytes(Mat mat)
    {
        var bytes = new byte[mat.Total() * mat.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        return bytes;
    }
}
