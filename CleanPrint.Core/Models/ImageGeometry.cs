using System;

namespace CleanPrint.Core.Models;

/// <summary>The eight resize handles of an image's box: four corners and four edge midpoints.</summary>
public enum ImageHandle
{
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
}

/// <summary>
/// The arithmetic of a picture placed on the page, free of any UI so it can be tested: how a drag of one of its eight handles changes its box,
/// which handle a point is on, how big a newly inserted picture starts out, and how to stand a phone photo upright.
///
/// A picture's box is described exactly like a text box: an ANCHOR (its top-left corner), a width and height, and a clockwise rotation about the anchor
/// — so everything here works in the box's own "local" frame (x along its top edge, y down its left edge, origin at the anchor) and uses
/// <see cref="TextGeometry"/> to go between that frame and the page.
/// </summary>
public static class ImageGeometry
{
    /// <summary>No side of a picture can be dragged smaller than this (page units): small enough to be a thumbnail, big enough to still grab.</summary>
    public const double MinSizeDiu = 12;

    /// <summary>The handles in the order they are tried when a point is near two at once (a tiny box): corners before edges.</summary>
    private static readonly ImageHandle[] HitOrder =
    {
        ImageHandle.TopLeft, ImageHandle.TopRight, ImageHandle.BottomRight, ImageHandle.BottomLeft,
        ImageHandle.Top, ImageHandle.Right, ImageHandle.Bottom, ImageHandle.Left,
    };

    private static bool IsLeft(ImageHandle h) => h is ImageHandle.TopLeft or ImageHandle.Left or ImageHandle.BottomLeft;
    private static bool IsRight(ImageHandle h) => h is ImageHandle.TopRight or ImageHandle.Right or ImageHandle.BottomRight;
    private static bool IsTop(ImageHandle h) => h is ImageHandle.TopLeft or ImageHandle.Top or ImageHandle.TopRight;
    private static bool IsBottom(ImageHandle h) => h is ImageHandle.BottomLeft or ImageHandle.Bottom or ImageHandle.BottomRight;

    /// <summary>Where a handle sits, in the box's local frame.</summary>
    public static GeoPoint HandlePosition(ImageHandle handle, double width, double height)
    {
        double x = IsLeft(handle) ? 0 : IsRight(handle) ? width : width / 2;
        double y = IsTop(handle) ? 0 : IsBottom(handle) ? height : height / 2;
        return new GeoPoint(x, y);
    }

    /// <summary>The handle a point (in the box's local frame) is on, if any: within <paramref name="tolerance"/> of one, corners first.</summary>
    public static ImageHandle? HitHandle(GeoPoint local, double width, double height, double tolerance)
    {
        foreach (var handle in HitOrder)
        {
            var p = HandlePosition(handle, width, height);
            double dx = local.X - p.X, dy = local.Y - p.Y;
            if (dx * dx + dy * dy <= tolerance * tolerance) return handle;
        }
        return null;
    }

    /// <summary>The handle on the other side: the one whose point stays put while this one is dragged.</summary>
    public static ImageHandle Opposite(ImageHandle handle) => handle switch
    {
        ImageHandle.TopLeft => ImageHandle.BottomRight,
        ImageHandle.Top => ImageHandle.Bottom,
        ImageHandle.TopRight => ImageHandle.BottomLeft,
        ImageHandle.Right => ImageHandle.Left,
        ImageHandle.BottomRight => ImageHandle.TopLeft,
        ImageHandle.Bottom => ImageHandle.Top,
        ImageHandle.BottomLeft => ImageHandle.TopRight,
        _ => ImageHandle.Right,
    };

    /// <summary>
    /// The box after dragging <paramref name="handle"/> to <paramref name="pointerPage"/> (a page point), as its new anchor, width and height. The rotation is
    /// unchanged. The side(s) opposite the handle stay exactly where they are on the page: a corner drag pivots on the opposite corner; an edge drag moves only
    /// that edge. No side goes below <paramref name="minSize"/>; the box cannot be dragged inside out.
    ///
    /// With <paramref name="keepAspect"/>, width and height stay in proportion: a corner drag scales the box about the opposite corner (following whichever of
    /// the pointer's two distances is the larger, so the box always reaches the pointer), and an edge drag scales it about the MIDDLE of the opposite edge
    /// (so it grows evenly to either side instead of lopsidedly).
    /// </summary>
    public static (GeoPoint Anchor, double Width, double Height) Resize(
        GeoPoint anchor, double rotationDegrees, double width, double height, ImageHandle handle, GeoPoint pointerPage, bool keepAspect, double minSize = MinSizeDiu)
    {
        var p = TextGeometry.PageToLocal(anchor, rotationDegrees, pointerPage);
        bool left = IsLeft(handle), right = IsRight(handle), top = IsTop(handle), bottom = IsBottom(handle);
        bool corner = (left || right) && (top || bottom);

        double x0 = 0, y0 = 0, x1 = width, y1 = height;
        if (left) x0 = Math.Min(p.X, x1 - minSize);
        if (right) x1 = Math.Max(p.X, x0 + minSize);
        if (top) y0 = Math.Min(p.Y, y1 - minSize);
        if (bottom) y1 = Math.Max(p.Y, y0 + minSize);

        if (keepAspect && width > 0 && height > 0)
        {
            double w = x1 - x0, h = y1 - y0;
            double floor = minSize / Math.Min(width, height);     // the scale at which the shorter side hits the minimum

            if (corner)
            {
                double s = Math.Max(Math.Max(w / width, h / height), floor);
                double newWidth = width * s, newHeight = height * s;
                if (left) x0 = x1 - newWidth; else x1 = x0 + newWidth;
                if (top) y0 = y1 - newHeight; else y1 = y0 + newHeight;
            }
            else if (left || right)
            {
                double s = Math.Max(w / width, floor);
                double newWidth = width * s, newHeight = height * s;
                if (left) x0 = x1 - newWidth; else x1 = x0 + newWidth;
                y0 = height / 2 - newHeight / 2;
                y1 = height / 2 + newHeight / 2;
            }
            else
            {
                double s = Math.Max(h / height, floor);
                double newWidth = width * s, newHeight = height * s;
                if (top) y0 = y1 - newHeight; else y1 = y0 + newHeight;
                x0 = width / 2 - newWidth / 2;
                x1 = width / 2 + newWidth / 2;
            }
        }

        // The new top-left, found in the OLD frame and carried to the page: the rotation is unchanged, so it is the new anchor.
        var newAnchor = TextGeometry.LocalToPage(anchor, rotationDegrees, new GeoPoint(x0, y0));
        return (newAnchor, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The size a picture starts at when it is placed: its natural size on paper (pixels at its own DPI, in page units; 96 DPI when it doesn't say), shrunk —
    /// never enlarged — to fit inside <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, keeping its proportions.
    /// </summary>
    public static (double Width, double Height) FitInside(double widthPx, double heightPx, double dpi, double maxWidth, double maxHeight)
    {
        if (widthPx <= 0 || heightPx <= 0) return (MinSizeDiu, MinSizeDiu);
        double perPixel = 96.0 / (dpi > 0 ? dpi : 96.0);
        double w = widthPx * perPixel, h = heightPx * perPixel;
        double scale = Math.Min(1.0, Math.Min(maxWidth / w, maxHeight / h));
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        return (Math.Max(MinSizeDiu, w * scale), Math.Max(MinSizeDiu, h * scale));
    }

    /// <summary>
    /// Where a point of the PICTURE (s across, t down, measured from the picture's own top-left in box units) ends up on the page: the picture is mirrored within
    /// its box if flipped, then the box is rotated clockwise about its anchor. This is what both the on-screen overlay and the print path draw.
    /// </summary>
    public static GeoPoint PagePositionOfContent(GeoPoint anchor, double rotationDegrees, double width, double height, bool flipHorizontal, bool flipVertical, double s, double t) =>
        TextGeometry.LocalToPage(anchor, rotationDegrees, new GeoPoint(flipHorizontal ? width - s : s, flipVertical ? height - t : t));

    /// <summary>
    /// A picture after the page is mirrored about a vertical line (<paramref name="horizontal"/>: x = axis) or a horizontal one (y = axis). Unlike text, a picture is
    /// MIRRORED, not kept readable: its box goes to the mirrored place exactly as a text box does (see <see cref="TextGeometry.MirrorTextBox"/> — the rotation is
    /// reflected and the anchor moves to the new top-left), and because that new box reads the other way round, the picture's own flip is toggled so that every
    /// point of the picture lands on the mirror image of where it was.
    /// </summary>
    public static (GeoPoint Anchor, double RotationDegrees, bool FlipHorizontal, bool FlipVertical) MirrorPicture(
        GeoPoint anchor, double rotationDegrees, double width, double height, bool flipHorizontal, bool flipVertical, bool horizontal, double axis)
    {
        var (newAnchor, newRotation) = TextGeometry.MirrorTextBox(anchor, rotationDegrees, width, height, horizontal, axis);
        return (newAnchor, newRotation, horizontal ? !flipHorizontal : flipHorizontal, horizontal ? flipVertical : !flipVertical);
    }

    /// <summary>
    /// How to stand a photo upright from its EXIF orientation tag (1–8; anything else means "already upright"): mirror it left-right FIRST if
    /// <c>MirrorFirst</c>, THEN rotate it <c>RotateClockwiseDegrees</c>. A phone stores its pixels as the sensor saw them and records here how it was held.
    /// </summary>
    public static (int RotateClockwiseDegrees, bool MirrorFirst) ExifUprightTransform(int orientation) => orientation switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),
        5 => (270, true),
        6 => (90, false),
        7 => (90, true),
        8 => (270, false),
        _ => (0, false),
    };
}
