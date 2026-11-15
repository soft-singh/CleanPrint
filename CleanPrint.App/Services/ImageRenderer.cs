using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>
/// Draws a picture annotation for the printed/exported page. The twin of TextRenderer.RasterizeForPrint and built the same way, so a picture lands exactly where
/// the preview shows it: scaled to page pixels, mirrored within its box if flipped, rotated clockwise about the box's top-left, at its opacity, into the smallest
/// bitmap that holds the result.
/// </summary>
public static class ImageRenderer
{
    /// <summary>
    /// Renders the picture into premultiplied-BGRA pixels ready to composite. <c>OffsetX/OffsetY</c> say where the bitmap's top-left sits relative to the
    /// annotation's anchor (negative when the rotation swings the picture up or left of it). Needs the UI thread (it renders through WPF). Null if there is
    /// nothing to draw or the result would be absurdly large.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int OffsetX, int OffsetY)? RasterizeForPrint(
        BitmapSource picture, double widthDiu, double heightDiu, bool flipHorizontal, bool flipVertical, double opacity, double rotationDegrees, double pixelsPerDiu)
    {
        double boxWidth = widthDiu * pixelsPerDiu, boxHeight = heightDiu * pixelsPerDiu;
        if (boxWidth < 1 || boxHeight < 1) return null;

        var bounds = TextGeometry.BoundsOf(TextGeometry.RectangleCorners(new GeoPoint(0, 0), rotationDegrees, 0, 0, boxWidth, boxHeight));
        int offsetX = (int)Math.Floor(bounds.MinX) - 1, offsetY = (int)Math.Floor(bounds.MinY) - 1;
        int width = (int)Math.Ceiling(bounds.MaxX) - offsetX + 1, height = (int)Math.Ceiling(bounds.MaxY) - offsetY + 1;
        if (width <= 2 || height <= 2 || (long)width * height > 120_000_000) return null;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality); // a large photo shrunk to print size should be averaged down, not sampled
        using (var context = visual.RenderOpen())
        {
            // Pushed outermost-first, applied to the drawing innermost-first: mirror within the box (about its centre), rotate about the anchor
            // (the origin), then shift into the bitmap. The opacity covers the whole picture.
            context.PushOpacity(Math.Clamp(opacity, 0, 1));
            context.PushTransform(new TranslateTransform(-offsetX, -offsetY));
            context.PushTransform(new RotateTransform(rotationDegrees));
            context.PushTransform(new ScaleTransform(flipHorizontal ? -1 : 1, flipVertical ? -1 : 1, boxWidth / 2, boxHeight / 2));
            context.DrawImage(picture, new Rect(0, 0, boxWidth, boxHeight));
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height, offsetX, offsetY);
    }
}
