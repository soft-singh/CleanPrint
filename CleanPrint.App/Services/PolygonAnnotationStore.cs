using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanPrint.Core.Models;
using CleanPrint.Core.Paths;

namespace CleanPrint.App.Services;

/// <summary>Persists polygon annotations (outline, purpose, color, thickness) so switching pages
/// or Before/After doesn't lose them. Stored per job at
/// %LocalAppData%\CleanPrint\Jobs\{jobId}\polygon-annotations.json.</summary>
public sealed class PolygonAnnotationStore
{
    private static string GetFilePath(string jobId) =>
        Path.Combine(CleanPrintPaths.JobFolder(jobId), "polygon-annotations.json");

    public Task<JobAnnotationSettings> LoadAsync(string jobId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var path = GetFilePath(jobId);
            if (!File.Exists(path))
                return new JobAnnotationSettings();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<JobAnnotationSettings>(json) ?? new JobAnnotationSettings();
            }
            catch
            {
                return new JobAnnotationSettings(); // corrupt/unreadable file — start fresh rather than crash
            }
        }, ct);
    }

    public Task SaveAsync(string jobId, JobAnnotationSettings settings, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var path = GetFilePath(jobId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }, ct);
    }
}
