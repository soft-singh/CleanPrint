using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Interfaces;
using CleanPrint.Core.Models;
using OpenCvSharp;

namespace CleanPrint.App.Services;

/// <summary>What the editor needs to start: the page's base image as it is now, and the processing service to preview effects with.</summary>
public sealed record BaseImageEditContext(CapturedPage RawPage, IImageProcessingService Processing, int PageNumber, int PageCount);

/// <summary>What the editor hands back when Save is pressed: the finished full-resolution page (effects applied, skew-corrected, turned, cropped, erased), and what was done to get there — which
/// is what the annotations need to follow it (<c>Skew</c> is the correction that was applied, if any). <c>EffectsBaked</c> is true if any effect that changes pixels was applied, in which case the main window's effect settings
/// (which described how to display the OLD image) no longer apply and are reset.</summary>
public sealed record BaseImageEditResult(CapturedPage NewPage, double AngleDegrees, PixelRect Crop, bool EffectsBaked, PerspectiveStage? Skew = null, bool HasErasures = false);

/// <summary>
/// The pixel work of editing a page's base image: turn it by an angle (the canvas grows to hold it, the new corners are white), crop it, apply effects, and write it back.
/// The order is effects, then turn, then crop — the order the editor's preview shows, so what is saved is what was seen. The geometry (canvas size, the matrix) comes from
/// BaseImageEditMath in Core, where it is tested, and is the same arithmetic that moves the annotations.
/// </summary>
public static class BaseImageEditService
{
    private static Mat ToMat(CapturedPage page)
    {
        var mat = new Mat(page.Height, page.Width, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(page.PixelData, 0, mat.Data, page.PixelData.Length);
        return mat;
    }

    private static byte[] ToBytes(Mat mat)
    {
        var bytes = new byte[mat.Total() * mat.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static CapturedPage From(CapturedPage like, Mat mat, int? dpi = null) => new()
    {
        PageNumber = like.PageNumber,
        Width = mat.Width,
        Height = mat.Height,
        Dpi = dpi ?? like.Dpi,
        PixelData = ToBytes(mat),
        SourceJobId = like.SourceJobId,
    };

    /// <summary>A copy no more than <paramref name="maxDimension"/> pixels on its longest side — what the editor previews, so dragging and effects stay quick on a big scan.
    /// Its DPI is scaled with it, so an effect whose strength is judged in physical size (whitening's kernel) looks the same on the small copy as on the real page.</summary>
    public static CapturedPage Downscale(CapturedPage page, int maxDimension)
    {
        int longest = Math.Max(page.Width, page.Height);
        if (longest <= maxDimension) return page;

        double scale = (double)maxDimension / longest;
        int width = Math.Max(1, (int)Math.Round(page.Width * scale)), height = Math.Max(1, (int)Math.Round(page.Height * scale));
        using var source = ToMat(page);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        return From(page, resized, Math.Max(1, (int)Math.Round(page.Dpi * scale)));
    }

    public static BitmapSource ToBitmap(CapturedPage page)
    {
        var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null, page.PixelData, page.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Turns the page clockwise by the angle. A quarter turn is exact (the pixels are just moved); any other angle is resampled (bicubic) onto a canvas just big
    /// enough to hold the whole turned image, with white where the image doesn't reach.</summary>
    public static CapturedPage Rotate(CapturedPage page, double angleDegrees)
    {
        double angle = BaseImageEditMath.NormalizeAngle(angleDegrees);
        if (Math.Abs(angle) < 1e-9) return page;

        using var source = ToMat(page);
        using var turned = new Mat();
        if (BaseImageEditMath.IsRightAngle(angle, out int quarters))
        {
            Cv2.Rotate(source, turned, quarters switch
            {
                1 => RotateFlags.Rotate90Clockwise,
                2 => RotateFlags.Rotate180,
                _ => RotateFlags.Rotate90Counterclockwise,
            });
        }
        else
        {
            var (width, height) = BaseImageEditMath.RotatedCanvasSize(page.Width, page.Height, angle);
            var m = BaseImageEditMath.WarpMatrix(page.Width, page.Height, width, height, angle);
            using var matrix = new Mat(2, 3, MatType.CV_64FC1);
            for (int i = 0; i < 6; i++) matrix.Set<double>(i / 3, i % 3, m[i]);
            Cv2.WarpAffine(source, turned, matrix, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
        }
        return From(page, turned);
    }

    /// <summary>
    /// Skew correction: warps the page so that the quad <paramref name="homography"/> was made from becomes a true rectangle of <paramref name="width"/> x <paramref name="height"/> (bicubic, white where
    /// the page doesn't reach). The matrix is converted to OpenCV's pixel-centre numbering (PerspectiveMath.ToPixelCentreConvention), so the result is exactly where the annotations are moved to.
    /// </summary>
    public static CapturedPage WarpPerspective(CapturedPage page, double[] homography, int width, int height)
    {
        using var source = ToMat(page);
        using var warped = new Mat();
        var m = PerspectiveMath.ToPixelCentreConvention(homography);
        using var matrix = new Mat(3, 3, MatType.CV_64FC1);
        for (int i = 0; i < 9; i++) matrix.Set<double>(i / 3, i % 3, m[i]);
        Cv2.WarpPerspective(source, warped, matrix, new Size(Math.Max(1, width), Math.Max(1, height)), InterpolationFlags.Cubic, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
        return From(page, warped);
    }

    /// <summary>
    /// Paints the erased areas white. The shapes are in the coordinates of the turned canvas; <paramref name="originX"/>/<paramref name="originY"/> is where the page's top-left sits in them (the crop's
    /// corner), so they are shifted to the page. Brush strokes are thick anti-aliased lines with a round dab at every point, so a stroke has smooth round ends and corners.
    /// </summary>
    public static CapturedPage Erase(CapturedPage page, IReadOnlyList<EraseShape> shapes, double originX, double originY)
    {
        if (shapes.Count == 0) return page;

        using var mat = ToMat(page);
        var white = new Scalar(255, 255, 255, 255);
        Point At(GeoPoint p) => new((int)Math.Round(p.X - originX), (int)Math.Round(p.Y - originY));

        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case EraseRect rectangle:
                {
                    int x = (int)Math.Round(rectangle.X - originX), y = (int)Math.Round(rectangle.Y - originY);
                    int width = Math.Max(1, (int)Math.Round(rectangle.Width)), height = Math.Max(1, (int)Math.Round(rectangle.Height));
                    Cv2.Rectangle(mat, new Rect(x, y, width, height), white, -1);
                    break;
                }
                case EraseStroke stroke when stroke.Points.Count > 0:
                {
                    int radius = Math.Max(1, (int)Math.Round(stroke.Radius));
                    for (int i = 0; i < stroke.Points.Count; i++)
                    {
                        Cv2.Circle(mat, At(stroke.Points[i]), radius, white, -1, LineTypes.AntiAlias);
                        if (i > 0) Cv2.Line(mat, At(stroke.Points[i - 1]), At(stroke.Points[i]), white, radius * 2, LineTypes.AntiAlias);
                    }
                    break;
                }
            }
        }
        return From(page, mat);
    }

    /// <summary>
    /// Keeps the given rectangle of the page. The rectangle may reach PAST the page on any side — that is how the canvas is enlarged — and the part of the new page the image doesn't cover
    /// is white. Which part of the image goes where is BaseImageEditMath.CropPlan (tested): the region of the image the rectangle covers, and the spot in the new page it lands on.
    /// </summary>
    public static CapturedPage Crop(CapturedPage page, PixelRect rect)
    {
        int width = Math.Max(1, rect.Width), height = Math.Max(1, rect.Height);
        if (rect.X == 0 && rect.Y == 0 && width == page.Width && height == page.Height) return page;

        using var source = ToMat(page);
        using var result = new Mat(height, width, MatType.CV_8UC4, new Scalar(255, 255, 255, 255)); // white, to start with
        if (BaseImageEditMath.CropPlan(new PixelRect(rect.X, rect.Y, width, height), page.Width, page.Height) is { } plan)
        {
            using var sourceRegion = new Mat(source, new Rect(plan.Source.X, plan.Source.Y, plan.Source.Width, plan.Source.Height));
            using var targetRegion = new Mat(result, new Rect(plan.DestinationX, plan.DestinationY, plan.Source.Width, plan.Source.Height));
            sourceRegion.CopyTo(targetRegion);   // targetRegion is a window onto `result`, so this writes into it
        }
        return From(page, result);
    }

    /// <summary>
    /// The finished page, in this order: effects (auto-crop never — the editor crops by hand), skew correction, turned, cropped, then the erased areas painted white. That is the order the
    /// editor's preview shows things in, so what is saved is what was seen.
    /// </summary>
    public static async Task<CapturedPage> RenderAsync(
        CapturedPage raw, double angleDegrees, PixelRect crop, ImageEffectSettings effects, IImageProcessingService processing,
        PerspectiveStage? skew = null, IReadOnlyList<EraseShape>? erasures = null, CancellationToken ct = default)
    {
        var settings = effects.Clone();
        settings.AutoCropEnabled = false;
        var page = settings.ChangesPixels ? await processing.ApplyEffectsAsync(raw, settings, ct) : raw;
        return await Task.Run(() =>
        {
            if (skew is not null) page = WarpPerspective(page, skew.Homography, skew.Width, skew.Height);
            var cropped = Crop(Rotate(page, angleDegrees), crop);
            return Erase(cropped, erasures ?? Array.Empty<EraseShape>(), crop.X, crop.Y);
        }, ct);
    }

    /// <summary>
    /// Writes the page to <paramref name="path"/> in the format its extension says (JPEG at the given quality, otherwise PNG), with its DPI recorded in the file. Written to a
    /// temporary file and moved over the original, so a failure part-way (a full disk) can never leave a half-written page.
    /// </summary>
    public static void WritePage(string path, CapturedPage page, int jpegQuality)
    {
        using var bgra = ToMat(page);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR); // the page is fully opaque: drop the alpha channel rather than store it
        var bitmap = BitmapSource.Create(page.Width, page.Height, page.Dpi, page.Dpi, PixelFormats.Bgr24, null, ToBytes(bgr), page.Width * 3);
        bitmap.Freeze();

        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = extension is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string temp = path + ".editing.tmp";
        using (var stream = File.Create(temp))
            encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }
}
