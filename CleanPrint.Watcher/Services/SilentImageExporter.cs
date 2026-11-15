using System;
using System.Collections.Generic;
using System.IO;
using CleanPrint.Core.Export;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace CleanPrint.Watcher.Services;

/// <summary>
/// SILENT EXPORT: saves a freshly captured job's pages straight to the folder the user chose, as image files in the format and with the names they chose, without CleanPrint's window ever
/// opening (see <see cref="SilentExportSettings"/>). The pages are saved exactly as captured — silent export does not apply the Effects (those belong to the window the user is skipping).
///
/// Uses OpenCV for the conversions, as the capture step already does for its own pages, so there is one imaging path and the same PNG-compression and JPEG-quality settings apply. A page already
/// in the wanted format (PNG to PNG, JPEG to JPEG) is simply copied, byte for byte.
/// </summary>
public static class SilentImageExporter
{
    /// <summary>Exports every page of the job. Returns the files written. A page that fails is logged and skipped; the others are still done.</summary>
    public static IReadOnlyList<string> Export(string jobFolder, string jobId, SilentExportSettings settings, ExportQualitySettings quality, ILogger logger)
    {
        var written = new List<string>();
        var pages = CleanPrintPaths.GetJobPageFiles(jobFolder);
        if (pages.Count == 0)
        {
            logger.LogWarning("Silent export: job {JobId} has no pages to export.", jobId);
            return written;
        }

        string folder = settings.EffectiveFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Silent export: cannot create the export folder {Folder}; job {JobId} was not exported.", folder, jobId);
            return written;
        }

        var format = ImageFormats.Find(ImageFormats.Silent, settings.Format);
        var now = DateTime.Now;   // one moment for the whole job, so every page of it carries the same date and time
        for (int i = 0; i < pages.Count; i++)
        {
            string name = SilentExportNaming.Build(settings.NamePattern, now, jobId, i + 1, pages.Count);
            string destination = SilentExportNaming.UniquePath(folder, name, format.Extension);
            try
            {
                WriteImage(pages[i], destination, format.Id, quality);
                written.Add(destination);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Silent export: failed to write page {Page} of job {JobId} to {Path}.", i + 1, jobId, destination);
                try { if (File.Exists(destination)) File.Delete(destination); } catch { /* a half-written file we couldn't remove: nothing more to do */ }
            }
        }

        logger.LogInformation("Silent export: {Count} of {Total} page(s) of job {JobId} saved to {Folder} as {Format}.", written.Count, pages.Count, jobId, folder, format.Id);
        return written;
    }

    private static void WriteImage(string source, string destination, string formatId, ExportQualitySettings quality)
    {
        string sourceExtension = Path.GetExtension(source).ToLowerInvariant();
        if ((formatId == "png" && sourceExtension == ".png") || (formatId == "jpg" && sourceExtension == ".jpg"))
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }

        // The bytes are read and written by .NET, and OpenCV only decodes/encodes them in memory: OpenCV's own path-based ImRead/ImWrite can fail on Windows when a path holds non-English
        // characters (a user name like "José" in the profile folder, an export folder with accents), and these paths are the user's.
        using var image = Cv2.ImDecode(File.ReadAllBytes(source), ImreadModes.Unchanged);
        if (image.Empty()) throw new IOException($"The captured page could not be read: {source}");

        // JPEG, BMP and TIFF are written without a transparency channel; PNG keeps whatever it has.
        using var flat = new Mat();
        Mat toWrite = image;
        if (formatId != "png" && image.Channels() == 4)
        {
            Cv2.CvtColor(image, flat, ColorConversionCodes.BGRA2BGR);
            toWrite = flat;
        }

        string extension = formatId switch { "jpg" => ".jpg", "bmp" => ".bmp", "tif" => ".tif", _ => ".png" };
        var parameters = formatId switch
        {
            "jpg" => new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(quality.JpgQuality, 1, 100)) },
            "png" => new[] { new ImageEncodingParam(ImwriteFlags.PngCompression, Math.Clamp(quality.PngCompressionLevel, 0, 9)) },
            _ => Array.Empty<ImageEncodingParam>(),   // bmp, tif: the format follows from the extension
        };
        // (The return value is not used: ImEncode returns a bool in some OpenCvSharp versions and nothing in others — 4.10, which this project uses, is one of the latter. An encode that
        // failed shows up as an empty buffer either way.)
        Cv2.ImEncode(extension, toWrite, out byte[] encoded, parameters);
        if (encoded is null || encoded.Length == 0)
            throw new IOException($"The image could not be encoded as {formatId}: {destination}");
        File.WriteAllBytes(destination, encoded);
    }
}
