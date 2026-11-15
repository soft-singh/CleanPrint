using System;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CleanPrint.Core.Paths;
using Microsoft.Extensions.Logging;

namespace CleanPrint.Watcher.Services;

public sealed class JobReadyEventArgs : EventArgs
{
    public required string JobId { get; init; }
    public required string JobFolder { get; init; }
    public required int PageCount { get; init; }
}

/// <summary>
/// Watches the fixed-port capture file for finished print jobs, waits until the job is genuinely
/// complete (not just momentarily unlocked — see WaitForFileReadyAndRelocateAsync), then rasterizes
/// every page straight to PNG files under a per-job folder. No in-memory page buffering, no image
/// processing — that's the App's job once it loads these PNGs.
/// </summary>
public sealed class XpsCaptureAndConvertService : IDisposable
{
    private readonly ILogger<XpsCaptureAndConvertService> _logger;
    private readonly int _renderDpi;

    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _processingGate = new(1, 1);

    // FileSystemWatcher raises events on an arbitrary ThreadPool worker thread, but WPF's
    // FixedPage/RenderTargetBitmap/XamlReader pipeline requires STA thread affinity — [STAThread]
    // on Main only governs the main thread, not wherever an event happens to fire. So all
    // rendering work is marshaled onto one dedicated STA thread with its own message loop.
    private Thread? _staThread;
    private Dispatcher? _staDispatcher;

    /// <summary>Raised once a job's pages have all been written to disk as PNGs.</summary>
    public event EventHandler<JobReadyEventArgs>? JobReady;

    public XpsCaptureAndConvertService(ILogger<XpsCaptureAndConvertService> logger, int renderDpi = 600)
    {
        _logger = logger;
        _renderDpi = renderDpi;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        CleanPrintPaths.EnsureDirectoriesExist();

        await StartStaRenderThreadAsync();

        var captureDir = Path.GetDirectoryName(CleanPrintPaths.CaptureFilePath)!;

        // Watch the whole directory rather than filtering to an exact filename: some drivers
        // delete+recreate the port file per job, or write to a temp name and rename it — those
        // surface as Created/Renamed events, not Changed, and filename-filtered watchers can miss
        // Renamed events depending on which side (old/new name) .NET matches against.
        _watcher = new FileSystemWatcher(captureDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnCaptureFileEvent;
        _watcher.Created += OnCaptureFileEvent;
        _watcher.Renamed += OnCaptureFileEvent;

        _logger.LogInformation("Watching for print jobs in {Dir} (target file: {File})", captureDir, CleanPrintPaths.CaptureFilePath);
    }

    /// <summary>Starts the dedicated STA thread and blocks until its Dispatcher is ready to accept work.</summary>
    private Task StartStaRenderThreadAsync()
    {
        var readyTcs = new TaskCompletionSource();

        _staThread = new Thread(() =>
        {
            _staDispatcher = Dispatcher.CurrentDispatcher;
            readyTcs.SetResult();
            Dispatcher.Run(); // pumps until _staDispatcher.InvokeShutdown() is called
        })
        {
            IsBackground = true,
            Name = "CleanPrint.Watcher STA Render Thread",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        return readyTcs.Task;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnCaptureFileEvent;
            _watcher.Created -= OnCaptureFileEvent;
            _watcher.Renamed -= OnCaptureFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }

        _staDispatcher?.InvokeShutdown();
        _staDispatcher = null;

        return Task.CompletedTask;
    }

    private async void OnCaptureFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.FullPath, CleanPrintPaths.CaptureFilePath, StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogDebug("Capture file event: {ChangeType} at {Path}", e.ChangeType, e.FullPath);

        // Debounce + avoid re-entrancy: only one job processed at a time.
        if (!await _processingGate.WaitAsync(0))
            return;

        try
        {
            var stableCopyPath = await WaitForFileReadyAndRelocateAsync(e.FullPath);
            if (stableCopyPath is null)
                return; // never stabilized into a valid package; a later event will retry

            var jobId = Path.GetFileNameWithoutExtension(stableCopyPath);
            var (jobFolder, pageCount) = RenderXpsToPngPages(stableCopyPath, jobId);

            JobReady?.Invoke(this, new JobReadyEventArgs { JobId = jobId, JobFolder = jobFolder, PageCount = pageCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process captured print job.");
        }
        finally
        {
            _processingGate.Release();
        }
    }

    /// <summary>
    /// The spooler/driver can write in multiple stages, briefly releasing its lock between them —
    /// grabbing the file at the first available lock can capture a truncated, incomplete package.
    /// This waits for the file size to stabilize across consecutive checks AND validates it's a
    /// well-formed ZIP/OPC package (what XPS is under the hood) before treating it as finished.
    /// </summary>
    private async Task<string?> WaitForFileReadyAndRelocateAsync(string path, int maxAttempts = 40)
    {
        long lastSize = -1;
        int stableCount = 0;
        string lastFailureReason = "never reached validation";

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(150);
                    continue;
                }

                var currentSize = new FileInfo(path).Length;
                if (currentSize == 0 || currentSize != lastSize)
                {
                    lastSize = currentSize;
                    stableCount = 0;
                    await Task.Delay(200);
                    continue; // still growing / just changed — not stable yet
                }

                stableCount++;
                if (stableCount < 2)
                {
                    await Task.Delay(200); // require two consecutive matching reads before trusting it
                    continue;
                }

                // Confirm nothing else has it open, then confirm it's actually a complete, valid
                // OPC/ZIP package (a partially-flushed XPS file will fail this even if unlocked).
                await using (var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    handle.Close();

                if (!IsValidXpsPackage(path, out var reason))
                {
                    var size = new FileInfo(path).Length;
                    lastFailureReason = $"{reason} ({size} bytes)";
                    _logger.LogDebug("File is unlocked but not yet a valid XPS package ({Size} bytes): {Reason}. Retrying.", size, reason);
                    await Task.Delay(200);
                    continue;
                }

                var uniqueName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.xps";
                var dest = Path.Combine(Path.GetDirectoryName(path)!, "_incoming", uniqueName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(path, dest);
                return dest;
            }
            catch (IOException)
            {
                await Task.Delay(150); // still locked by the spooler; retry
            }
        }

        _logger.LogWarning("Timed out waiting for print job file to become ready: {Path}. Last failure reason: {Reason}", path, lastFailureReason);
        return null;
    }

    private static bool IsValidXpsPackage(string path, out string reason)
    {
        // Cheap pre-check: XPS is a ZIP/OPC package under the hood, so it must start with a ZIP
        // local-file-header signature ("PK\x03\x04") or an empty-archive signature ("PK\x05\x06").
        // If this fails, the file isn't a ZIP at all — no point even trying XpsDocument.
        try
        {
            Span<byte> header = stackalloc byte[4];
            using (var fs = File.OpenRead(path))
            {
                int read = fs.Read(header);
                if (read < 4)
                {
                    reason = $"file too short to contain a ZIP header (only {read} byte(s))";
                    return false;
                }
            }

            bool isZipSignature =
                header[0] == 0x50 && header[1] == 0x4B && // 'P' 'K'
                ((header[2] == 0x03 && header[3] == 0x04) ||  // local file header
                 (header[2] == 0x05 && header[3] == 0x06));   // empty archive

            if (!isZipSignature)
            {
                reason = $"first 4 bytes are not a ZIP signature (got {header[0]:X2} {header[1]:X2} {header[2]:X2} {header[3]:X2} instead of 'PK..')";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"could not read header: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        try
        {
            // NOTE: deliberately NOT using XpsDocument.GetFixedDocumentSequence() here — it's
            // known to return null for packages written by the "Microsoft XPS Document Writer v4"
            // driver even when the package is perfectly valid (see RenderXpsToPngPages for the
            // full explanation). Instead, just confirm the ZIP contains at least one FixedPage
            // part, which is a reliable signal the package finished writing successfully.
            using var archive = ZipFile.OpenRead(path);
            bool hasPagePart = archive.Entries.Any(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));

            if (!hasPagePart)
            {
                reason = "ZIP is valid but contains no .fpage (FixedPage) parts";
                return false;
            }

            reason = "valid";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"could not read ZIP entries: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private (string JobFolder, int PageCount) RenderXpsToPngPages(string xpsPath, string jobId)
    {
        var jobFolder = Path.Combine(CleanPrintPaths.JobsRoot, jobId);
        Directory.CreateDirectory(jobFolder);

        // XpsDocument.GetFixedDocumentSequence() is unreliable here (see IsValidXpsPackage) — the
        // .NET-ported WPF packaging code doesn't recognize the relationship types the v4 XPS
        // driver writes, even though the package is completely valid. Work around it by reading
        // at the raw Packaging level: register the package with PackageStore (so pack:// URIs
        // still resolve for embedded fonts/images within each page), then find every FixedPage
        // part directly by its part name instead of walking the (broken) document-sequence
        // relationships.
        var packageUri = new Uri($"pack://cleanprint-{Guid.NewGuid():N}/");
        int pageNumber;

        // Read fresh per job (not cached at startup) since this is a long-running background
        // process — the person can change these via the App's Settings dialog while the Watcher is
        // already running, and this picks it up on the very next job without needing a restart.
        // Cheap: one small file read per print job, not per page. CaptureDpi's own default (300,
        // not the constructor's 600) applies on first run / if the settings file is missing —
        // fresh installs get the smaller-file-size behavior without needing to configure anything.
        var quality = CleanPrint.Core.Services.ExportQualitySettingsService.Load() ?? new CleanPrint.Core.Models.ExportQualitySettings();
        int renderDpi = quality.CaptureDpi > 0 ? quality.CaptureDpi : _renderDpi;
        int pngCompressionLevel = quality.PngCompressionLevel;
        bool useJpeg = quality.UseJpegForCapture;
        int jpgQuality = quality.JpgQuality;

        // Package must be fully closed (end of this `using` block) BEFORE we try to delete
        // xpsPath below — deleting while the package still holds the file open would silently
        // fail every time and leave orphaned .xps files behind indefinitely.
        using (var package = Package.Open(xpsPath, FileMode.Open, FileAccess.Read))
        {
            PackageStore.AddPackage(packageUri, package);
            try
            {
                // Everything inside RenderPagesFromPackage touches WPF FrameworkElement/visual
                // APIs, which require the dedicated STA thread — OnCaptureFileEvent runs on a
                // ThreadPool worker thread (FileSystemWatcher's callback thread), so this call
                // must be marshaled rather than made directly.
                pageNumber = _staDispatcher!.Invoke(() =>
                    RenderPagesFromPackage(package, packageUri, jobFolder, renderDpi, pngCompressionLevel, useJpeg, jpgQuality));
            }
            finally
            {
                PackageStore.RemovePackage(packageUri);
            }
        }

        try { File.Delete(xpsPath); } catch { /* best effort cleanup */ }

        // Written after every page is saved (not before) so a job that fails partway through
        // capture never ends up with a sidecar claiming a DPI for pages that don't actually exist.
        CleanPrint.Core.Services.JobCaptureInfoService.Save(jobFolder, renderDpi);

        _logger.LogInformation("Rendered {Count} page(s) from job {JobId} at {Dpi} DPI ({Format}) -> {Folder}",
            pageNumber, jobId, renderDpi, useJpeg ? "JPG" : "PNG", jobFolder);

        return (jobFolder, pageNumber);
    }

    /// <summary>The v4 print driver emits OpenXPS (ECMA-388) markup, whose FixedPage namespace
    /// differs from the classic Microsoft XPS namespace WPF's XamlReader has type mappings for.
    /// The two schemas are otherwise structurally identical, so remapping the namespace URI text
    /// before parsing is sufficient — confirmed by the exact "Cannot create unknown type
    /// '{http://schemas.openxps.org/oxps/v1.0}FixedPage'" exception this was built to fix.</summary>
    private const string OpenXpsNamespace = "http://schemas.openxps.org/oxps/v1.0";
    private const string MicrosoftXpsNamespace = "http://schemas.microsoft.com/xps/2005/06";

    private int RenderPagesFromPackage(Package package, Uri packageUri, string jobFolder, int renderDpi, int pngCompressionLevel, bool useJpeg, int jpgQuality)
    {
        var pageParts = package.GetParts()
            .Where(p => p.Uri.ToString().EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
            .Select(p => new { Part = p, Key = ExtractPageSortKey(p.Uri.ToString()) })
            .OrderBy(x => x.Key.DocIndex)
            .ThenBy(x => x.Key.PageIndex)
            .Select(x => x.Part)
            .ToList();

        if (pageParts.Count == 0)
            throw new InvalidOperationException("No FixedPage parts found in the captured XPS package.");

        double scale = renderDpi / 96.0; // WPF's device-independent unit is 1/96".
        int pageNumber = 0;

        foreach (var part in pageParts)
        {
            pageNumber++;

            var pageUri = PackUriHelper.Create(packageUri, part.Uri);
            var fixedPage = LoadFixedPage(part, pageUri);

            int pixelWidth = (int)(fixedPage.Width * scale);
            int pixelHeight = (int)(fixedPage.Height * scale);

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, renderDpi, renderDpi, PixelFormats.Pbgra32);
            fixedPage.Measure(new Size(fixedPage.Width, fixedPage.Height));
            fixedPage.Arrange(new Rect(0, 0, fixedPage.Width, fixedPage.Height));
            fixedPage.UpdateLayout();
            rtb.Render(fixedPage);

            string extension = useJpeg ? "jpg" : "png";
            var pagePath = Path.Combine(jobFolder, $"page_{pageNumber:D3}.{extension}");
            if (useJpeg)
                SaveJpegWithConfiguredQuality(rtb, pagePath, jpgQuality);
            else
                SavePngWithConfiguredCompression(rtb, pagePath, pngCompressionLevel);
        }

        return pageNumber;
    }

    /// <summary>
    /// Saves a rendered page as JPG at the configured quality — the lossy alternative to
    /// SavePngWithConfiguredCompression below, opted into via ExportQualitySettings.
    /// UseJpegForCapture. Much bigger size reduction than PNG compression tuning alone, at the
    /// cost of some baked-in generational quality loss from the very first save.
    /// </summary>
    private static void SaveJpegWithConfiguredQuality(RenderTargetBitmap rtb, string jpgPath, int quality)
    {
        int width = rtb.PixelWidth;
        int height = rtb.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[height * stride];
        rtb.CopyPixels(pixels, stride, 0);

        using var mat = new OpenCvSharp.Mat(height, width, OpenCvSharp.MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);

        OpenCvSharp.Cv2.ImWrite(jpgPath, mat,
            new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, quality));
    }

    /// <summary>
    /// Saves a rendered page as PNG with the configured compression level — WPF's own
    /// PngBitmapEncoder has no such setting (it's always lossless, but offers no control over the
    /// speed/size trade-off), so this goes via OpenCvSharp instead, which does.
    ///
    /// RenderTargetBitmap here always uses Pbgra32 (premultiplied alpha) — copying those bytes
    /// directly into a BGRA Mat without un-premultiplying would shift colors wherever alpha < 255,
    /// but every page rendered here is fully opaque (alpha 255 throughout, same assumption already
    /// made everywhere else in this app for captured pages), and premultiplying by 1.0 is a no-op,
    /// so this is safe in practice for this specific use even though the two formats aren't
    /// generally interchangeable.
    /// </summary>
    /// <summary>
    /// Saves a rendered page as PNG with the configured compression level — WPF's own
    /// PngBitmapEncoder has no such setting (it's always lossless, but offers no control over the
    /// speed/size trade-off), so this goes via OpenCvSharp instead, which does.
    ///
    /// RenderTargetBitmap here always uses Pbgra32 (premultiplied alpha) — copying those bytes
    /// directly into a BGRA Mat without un-premultiplying would shift colors wherever alpha < 255,
    /// but every page rendered here is fully opaque (alpha 255 throughout, same assumption already
    /// made everywhere else in this app for captured pages), and premultiplying by 1.0 is a no-op,
    /// so this is safe in practice for this specific use even though the two formats aren't
    /// generally interchangeable.
    ///
    /// The alpha channel is then dropped entirely (BGRA -> BGR) before encoding — since it's always
    /// fully opaque here, it carries no actual information, and removing it both shrinks the raw
    /// data 25% before compression even starts and tends to improve the compression ratio on what's
    /// left (one less channel for the row filter to reason about). PngPageLoader.Load already
    /// upgrades whatever it reads back to BGRA32 via FormatConvertedBitmap regardless of the
    /// source's actual channel count, so a 3-channel PNG here is completely transparent to the rest
    /// of the app — nothing downstream needs to know or care that alpha wasn't stored.
    /// </summary>
    private static void SavePngWithConfiguredCompression(RenderTargetBitmap rtb, string pngPath, int compressionLevel)
    {
        int width = rtb.PixelWidth;
        int height = rtb.PixelHeight;
        int stride = width * 4; // BGRA/PBGRA = 4 bytes/pixel
        var pixels = new byte[height * stride];
        rtb.CopyPixels(pixels, stride, 0);

        using var bgraMat = new OpenCvSharp.Mat(height, width, OpenCvSharp.MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bgraMat.Data, pixels.Length);

        using var bgrMat = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgraMat, bgrMat, OpenCvSharp.ColorConversionCodes.BGRA2BGR);

        OpenCvSharp.Cv2.ImWrite(pngPath, bgrMat,
            new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.PngCompression, compressionLevel));
    }

    private FixedPage LoadFixedPage(PackagePart part, Uri pageUri)
    {
        string xaml;
        using (var rawStream = part.GetStream())
        using (var reader = new StreamReader(rawStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            xaml = reader.ReadToEnd();
        }

        if (xaml.Contains(OpenXpsNamespace, StringComparison.Ordinal))
        {
            // Simple substring replace is safe here: every OpenXPS namespace variant (the main
            // FixedPage namespace, plus any sub-paths like ".../resourcedictionary-key") shares
            // this same prefix, and replacing the prefix maps each one onto its Microsoft-XPS
            // equivalent, which uses the identical sub-path structure.
            xaml = xaml.Replace(OpenXpsNamespace, MicrosoftXpsNamespace, StringComparison.Ordinal);
            _logger.LogDebug("Remapped OpenXPS namespace to Microsoft XPS namespace for {PageUri}", pageUri);
        }

        using var patchedStream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
        return (FixedPage)XamlReader.Load(patchedStream, new ParserContext { BaseUri = pageUri });
    }

    /// <summary>
    /// Extracts a (document index, page index) sort key from a standard XPS part path like
    /// "/Documents/1/Pages/1.fpage". Falls back to a stable (but not necessarily correct) order
    /// for non-standard layouts rather than throwing, since a best-effort page order beats failing
    /// the whole job outright.
    /// </summary>
    private static (int DocIndex, int PageIndex) ExtractPageSortKey(string partUri)
    {
        var match = Regex.Match(partUri, @"/Documents/(\d+)/Pages/(\d+)\.fpage$", RegexOptions.IgnoreCase);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out int docIndex) &&
            int.TryParse(match.Groups[2].Value, out int pageIndex))
        {
            return (docIndex, pageIndex);
        }

        return (0, 0); // unrecognized layout — caller's .ToList() preserves discovery order as a fallback
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _staDispatcher?.InvokeShutdown();
        _processingGate.Dispose();
    }
}
