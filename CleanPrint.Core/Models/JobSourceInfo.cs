using System;
using System.Collections.Generic;

namespace CleanPrint.Core.Models;

/// <summary>Records the original source image file path(s) a job was created from via "Open
/// Image(s)" — stored as source-images.json inside the job's own folder. Lets a later "Open
/// Image(s)" call detect that a given file has already been turned into a job before, and lets the
/// job browser search by original filename, not just the job's own generated folder name.
/// Print-captured jobs (from the Watcher) never have this file, since they don't originate from a
/// source image on disk.</summary>
public sealed class JobSourceInfo
{
    public List<string> SourceImagePaths { get; set; } = new();

    /// <summary>Each source path's LastWriteTimeUtc at the moment it was linked to this job — lets
    /// a later "Open Image(s)" call on the same path (see JobSourceService.HasSourceImageChanged)
    /// detect that the file has been edited/replaced since, and offer to update the job's own page
    /// with the new content instead of silently reopening the now-stale capture. A path with no
    /// entry here (including every job saved before this field existed) is treated as "unknown,
    /// assume unchanged" rather than as a false positive.</summary>
    public Dictionary<string, DateTime> LastKnownWriteTimesUtc { get; set; } = new();
}
