using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkA.Activities.HeavyProcessing.Activities;

public class PrepareSourceActivities
{
    private readonly ILogger<PrepareSourceActivities> _logger;

    public PrepareSourceActivities(ILogger<PrepareSourceActivities> logger)
    {
        _logger = logger;
    }

    [Activity]
    public async Task<PreparedSource> PrepareSourceAsync(WorkflowConfiguration config)
    {
        _logger.LogInformation("Preparing source for job {JobId} from {SourcePath}", config.JobId, config.SourcePath);

        var ext = Path.GetExtension(config.SourcePath).TrimStart('.').ToLowerInvariant();

        string workDir;
        string packageType;
        string originalPackageName;

        if (ext == "zip" && File.Exists(config.SourcePath))
        {
            packageType = "zip";
            originalPackageName = Path.GetFileName(config.SourcePath);
            workDir = Path.Combine(Path.GetTempPath(), $"dintinct_{config.JobId}");
            Directory.CreateDirectory(workDir);
            ZipFile.ExtractToDirectory(config.SourcePath, workDir, overwriteFiles: true);
            _logger.LogInformation("Extracted ZIP to temp dir {WorkDir}", workDir);
        }
        else if (Directory.Exists(config.SourcePath))
        {
            packageType = "folder";
            originalPackageName = Path.GetFileName(config.SourcePath.TrimEnd(Path.DirectorySeparatorChar));
            workDir = Path.Combine(Path.GetTempPath(), $"dintinct_{config.JobId}");
            CopyDirectory(config.SourcePath, workDir);
            _logger.LogInformation("Copied folder to temp dir {WorkDir}", workDir);
        }
        else if (File.Exists(config.SourcePath))
        {
            packageType = ext;
            originalPackageName = Path.GetFileName(config.SourcePath);
            workDir = Path.GetDirectoryName(config.SourcePath)!;

            _logger.LogInformation("Single-file source: {SourcePath}", config.SourcePath);
            return new PreparedSource(
                WorkDir: workDir,
                PackageType: packageType,
                OriginalPackageName: originalPackageName,
                SourceFiles: [config.SourcePath],
                NestedArchives: []);
        }
        else
        {
            throw new FileNotFoundException($"SourcePath not found: {config.SourcePath}");
        }

        var nestedArchives = new List<string>();
        await ExpandNestedArchivesAsync(workDir, workDir, nestedArchives);

        var sourceFiles = Directory
            .EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
            .ToList();

        _logger.LogInformation(
            "Job {JobId}: {FileCount} files, {ArchiveCount} nested archives in prepared source",
            config.JobId, sourceFiles.Count, nestedArchives.Count);

        return new PreparedSource(
            WorkDir: workDir,
            PackageType: packageType,
            OriginalPackageName: originalPackageName,
            SourceFiles: sourceFiles,
            NestedArchives: nestedArchives);
    }

    // Extracts each .zip found in `dir` into a same-named directory, then recurses into both
    // the expanded archive directories and any pre-existing regular subdirectories so that
    // archives nested at any depth inside plain folders are also expanded.
    // Callers always pass a temp work dir — the original config.SourcePath is never touched.
    private async Task ExpandNestedArchivesAsync(string dir, string rootDir, List<string> nestedArchives)
    {
        // Snapshot regular subdirectories before expansion so we don't double-visit
        // directories that were themselves zip files moments later.
        var existingSubDirs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).ToList();

        foreach (var zipFile in Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly).ToList())
        {
            var relPath = Path.GetRelativePath(rootDir, zipFile).Replace('\\', '/');
            nestedArchives.Add(relPath);

            _logger.LogInformation("Expanding nested archive {RelPath}", relPath);

            var tempDir = zipFile + "_tmp";
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipFile, tempDir, overwriteFiles: true);
            File.Delete(zipFile);

            // Rename temp dir to the original zip path — now a directory named e.g. "nested.zip"
            Directory.Move(tempDir, zipFile);

            await ExpandNestedArchivesAsync(zipFile, rootDir, nestedArchives);
        }

        // Recurse into regular subdirectories that existed before zip expansion.
        foreach (var subDir in existingSubDirs)
        {
            await ExpandNestedArchivesAsync(subDir, rootDir, nestedArchives);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var subDir in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            CopyDirectory(subDir, Path.Combine(destination, Path.GetFileName(subDir)));
    }
}
