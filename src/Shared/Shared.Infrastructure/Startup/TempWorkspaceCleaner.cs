using Microsoft.Extensions.Logging;

namespace Shared.Infrastructure.Startup;

/// <summary>
/// Deletes leftover "dintinct_" temp files and work directories from previous runs.
///
/// Activities delete their own temp artifacts in a finally block, but a worker that is
/// killed mid-activity (rebuild, crash, container restart) never runs those, so every
/// abandoned run strands its intermediates in the system temp directory — FFmpeg outputs
/// and full copies of source packages, hundreds of MB each.
///
/// Sweeping at worker startup is safe because nothing owns the previous run's files any
/// more. The <paramref name="minimumAge"/> guard exists because Network A and Network B
/// workers share one temp directory and one prefix: without it, a worker starting up
/// could delete a sibling worker's in-flight intermediates.
/// </summary>
public static class TempWorkspaceCleaner
{
    private const string Prefix = "dintinct_";

    /// <summary>
    /// Age below which an entry is left alone. Must comfortably exceed the longest
    /// realistic activity duration, since a sibling worker may still be writing to it.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(6);

    /// <summary>
    /// Deletes stale temp entries and returns the number of bytes reclaimed.
    /// Never throws — a failed sweep must not stop a worker from starting.
    /// </summary>
    public static long Sweep(ILogger logger, TimeSpan? minimumAge = null)
    {
        var age = minimumAge ?? DefaultMinimumAge;
        var cutoff = DateTime.UtcNow - age;
        var tempRoot = Path.GetTempPath();

        long reclaimed = 0;
        var deleted = 0;
        var skipped = 0;

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(tempRoot, $"{Prefix}*"))
            {
                try
                {
                    var info = Directory.Exists(path)
                        ? (FileSystemInfo)new DirectoryInfo(path)
                        : new FileInfo(path);

                    // A directory's own timestamps do not move when nested files change, so
                    // take the later of the two and rely on minimumAge for the safety margin.
                    var touched = info.LastWriteTimeUtc > info.CreationTimeUtc
                        ? info.LastWriteTimeUtc
                        : info.CreationTimeUtc;

                    if (touched > cutoff)
                    {
                        skipped++;
                        continue;
                    }

                    var size = MeasureSize(info);

                    if (info is DirectoryInfo dir)
                        dir.Delete(recursive: true);
                    else
                        info.Delete();

                    reclaimed += size;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still locked, or not ours to delete — the next startup will retry.
                    logger.LogDebug(ex, "Could not delete stale temp entry {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Temp sweep of {TempRoot} failed", tempRoot);
            return reclaimed;
        }

        if (deleted > 0)
            logger.LogInformation(
                "Temp sweep: removed {Deleted} stale entr(ies) older than {Hours}h from {TempRoot}, reclaimed {MB:N1} MB ({Skipped} still in use)",
                deleted, age.TotalHours, tempRoot, reclaimed / 1024.0 / 1024.0, skipped);
        else
            logger.LogInformation(
                "Temp sweep: nothing older than {Hours}h to remove from {TempRoot} ({Skipped} entr(ies) left alone)",
                age.TotalHours, tempRoot, skipped);

        return reclaimed;
    }

    private static long MeasureSize(FileSystemInfo info)
    {
        try
        {
            return info switch
            {
                FileInfo file => file.Length,
                DirectoryInfo dir => dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length),
                _ => 0
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
