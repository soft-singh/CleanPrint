using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Services;

/// <summary>What importing a picture produced: the name it is stored under, and its size (after standing it upright) and resolution.</summary>
public sealed record ImportedImage(string FileName, int WidthPx, int HeightPx, double Dpi);

/// <summary>
/// The pictures placed on a job's pages. Each one is COPIED into the job's own "images" folder when it is inserted, under a new unique name — so the job keeps
/// working if the original file is moved, edited or deleted, travels with the job, and goes when the job is deleted. A picture annotation refers to its
/// file by that name only (PolygonAnnotation.ImageFile).
/// </summary>
public static class JobImageStore
{
    public const string FolderName = "images";

    /// <summary>A ceiling on what is accepted (width x height): beyond this a picture would take more memory to show and print than a page itself may.</summary>
    public const long MaxPixels = 120_000_000;

    public static string FolderPath(string jobFolder) => Path.Combine(jobFolder, FolderName);

    /// <summary>The full path of a stored picture. Only the file-name part of what is passed is used, so a name can never point outside the folder.</summary>
    public static string FilePath(string jobFolder, string fileName) => Path.Combine(FolderPath(jobFolder), Path.GetFileName(fileName));

    /// <summary>Copies a picture file into the job. A PNG or JPEG that is already the right way up is copied byte for byte (no recompression); anything else —
    /// another format, or a photo whose camera recorded that it was held sideways — is stored as a PNG, stood upright. Throws if it can't be read.</summary>
    public static ImportedImage ImportFile(string jobFolder, string sourcePath)
    {
        BitmapFrame frame;
        using (var stream = File.OpenRead(sourcePath))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new InvalidOperationException($"That picture is {frame.PixelWidth:N0} × {frame.PixelHeight:N0} pixels — larger than the {MaxPixels / 1_000_000} megapixel limit.");

        double dpi = frame.DpiX > 1 ? frame.DpiX : 96;
        int orientation = ReadExifOrientation(frame);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        Directory.CreateDirectory(FolderPath(jobFolder));
        string baseName = Guid.NewGuid().ToString("N");

        if (orientation <= 1 && (extension is ".png" or ".jpg" or ".jpeg"))
        {
            string fileName = baseName + (extension == ".jpeg" ? ".jpg" : extension);
            File.Copy(sourcePath, FilePath(jobFolder, fileName));
            return new ImportedImage(fileName, frame.PixelWidth, frame.PixelHeight, dpi);
        }

        BitmapSource upright = frame;
        var (rotate, mirrorFirst) = ImageGeometry.ExifUprightTransform(orientation);
        if (rotate != 0 || mirrorFirst)
        {
            var transform = new TransformGroup();
            if (mirrorFirst) transform.Children.Add(new ScaleTransform(-1, 1));
            if (rotate != 0) transform.Children.Add(new RotateTransform(rotate));
            upright = new TransformedBitmap(frame, transform);
        }
        return WritePng(jobFolder, baseName, upright, dpi);
    }

    /// <summary>Stores a picture that has no file — one pasted from the clipboard — as a PNG.</summary>
    public static ImportedImage ImportBitmap(string jobFolder, BitmapSource bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxPixels)
            throw new InvalidOperationException($"That picture is larger than the {MaxPixels / 1_000_000} megapixel limit.");
        Directory.CreateDirectory(FolderPath(jobFolder));
        return WritePng(jobFolder, Guid.NewGuid().ToString("N"), bitmap, bitmap.DpiX > 1 ? bitmap.DpiX : 96);
    }

    private static ImportedImage WritePng(string jobFolder, string baseName, BitmapSource bitmap, double dpi)
    {
        string fileName = baseName + ".png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(FilePath(jobFolder, fileName)))
            encoder.Save(stream);
        return new ImportedImage(fileName, bitmap.PixelWidth, bitmap.PixelHeight, dpi);
    }

    /// <summary>Copies a stored picture from one job into another (when annotations are pasted across jobs). False if the source file isn't there.
    /// A copy already there is left alone: a file name is unique, so it is the same picture.</summary>
    public static bool CopyAsset(string fromJobFolder, string toJobFolder, string fileName)
    {
        string source = FilePath(fromJobFolder, fileName), destination = FilePath(toJobFolder, fileName);
        if (File.Exists(destination)) return true;
        if (!File.Exists(source)) return false;
        Directory.CreateDirectory(FolderPath(toJobFolder));
        File.Copy(source, destination);
        return true;
    }

    /// <summary>Decodes a stored picture, frozen so any thread may use it. <paramref name="maxDecodeWidth"/> (0 = none) limits how many pixels wide it is decoded
    /// — the on-screen overlay never needs more than a couple of thousand, and a big photo at full size is a lot of memory; printing decodes at full size.
    /// Null if the file is missing or unreadable.</summary>
    public static BitmapSource? Load(string jobFolder, string fileName, int maxDecodeWidth = 0)
    {
        try
        {
            string path = FilePath(jobFolder, fileName);
            if (!File.Exists(path)) return null;

            int naturalWidth;
            using (var probe = File.OpenRead(path))
                naturalWidth = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;                    // read it all now, so the file isn't held open
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            if (maxDecodeWidth > 0 && naturalWidth > maxDecodeWidth) image.DecodePixelWidth = maxDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The EXIF orientation tag (1-8), or 1 if the picture has none or it can't be read.</summary>
    private static int ReadExifOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })   // JPEG, then TIFF
        {
            try
            {
                if (metadata.GetQuery(query) is { } value && int.TryParse(value.ToString(), out int orientation) && orientation is >= 1 and <= 8)
                    return orientation;
            }
            catch
            {
                // this format has no such block: try the next
            }
        }
        return 1;
    }
}
