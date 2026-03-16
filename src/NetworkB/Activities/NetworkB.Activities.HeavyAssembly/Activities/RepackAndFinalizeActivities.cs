using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.HeavyAssembly.Activities;

public class RepackAndFinalizeActivities
{
    private readonly ILogger<RepackAndFinalizeActivities> _logger;

    public RepackAndFinalizeActivities(ILogger<RepackAndFinalizeActivities> logger)
    {
        _logger = logger;
    }

    [Activity]
    public Task RepackAndFinalizeAsync(AssemblyBlueprint blueprint, string assemblyDir)
    {
        // Re-create nested archives (deepest first so inner archives are present before outer ones zip them)
        foreach (var archivePath in blueprint.NestedArchives.OrderByDescending(p => p.Count(c => c == '/')))
        {
            ActivityExecutionContext.Current.Heartbeat(archivePath);

            var archiveDir = Path.Combine(assemblyDir, archivePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(archiveDir))
            {
                _logger.LogWarning("Expected nested archive directory not found: {ArchiveDir}", archiveDir);
                continue;
            }

            var tempZip = archiveDir + ".tmp";
            ZipFile.CreateFromDirectory(archiveDir, tempZip);
            Directory.Delete(archiveDir, recursive: true);
            File.Move(tempZip, archiveDir);
            _logger.LogInformation("Rebuilt nested archive: {ArchivePath}", archivePath);
        }

        ActivityExecutionContext.Current.Heartbeat("repack");

        // Repack into a ZIP archive if the original package was a ZIP
        if (blueprint.PackageType == "zip")
        {
            var zipPath = Path.Combine(blueprint.TargetPath, blueprint.OriginalPackageName);
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(assemblyDir, zipPath);
            _logger.LogInformation("Repacked ZIP archive: {ZipPath}", zipPath);

            try { Directory.Delete(assemblyDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
        else
        {
            // For folder packages, preserve the root directory name so the recipient gets the
            // exact same tree (TargetPath/OriginalPackageName/...). For single-file packages,
            // place the file directly inside TargetPath.
            var folderRoot = blueprint.PackageType == "folder"
                ? Path.Combine(blueprint.TargetPath, blueprint.OriginalPackageName)
                : blueprint.TargetPath;

            foreach (var assembled in Directory.EnumerateFiles(assemblyDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(assemblyDir, assembled);
                var dest = Path.Combine(folderRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(assembled, dest, overwrite: true);
            }
            try { Directory.Delete(assemblyDir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        _logger.LogInformation("Repack and finalize complete for job {JobId}", blueprint.Id);

        return Task.CompletedTask;
    }
}
