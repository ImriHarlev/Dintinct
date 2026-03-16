using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.HeavyAssembly.Activities;

public class AssembleFilesActivities
{
    private readonly ILogger<AssembleFilesActivities> _logger;

    public AssembleFilesActivities(ILogger<AssembleFilesActivities> logger)
    {
        _logger = logger;
    }

    [Activity]
    public async Task<AssembleFilesResult> AssembleFilesAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<string> receivedChunkPaths)
    {
        _logger.LogInformation("Assembling job {JobId} with {ChunkCount} received chunks",
            blueprint.Id, receivedChunkPaths.Count);

        var assemblyDir = Path.Combine(blueprint.TargetPath, $"_assembly_{blueprint.Id}");
        Directory.CreateDirectory(assemblyDir);

        var results = new List<FileResult>();

        foreach (var file in blueprint.Files)
        {
            ActivityExecutionContext.Current.Heartbeat(file.OriginalRelativePath);

            var allChunkNames = file.ConvertedFiles
                .SelectMany(cf => cf.Chunks)
                .Select(c => c.Name)
                .ToHashSet();

            if (allChunkNames.Any(c => blueprint.HardFailedChunkNames.Contains(c)))
            {
                results.Add(new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath));
                continue;
            }

            if (allChunkNames.Any(c => blueprint.UnsupportedChunkNames.Contains(c)))
            {
                results.Add(new FileResult(FileTransferStatus.NotSupported, file.OriginalRelativePath));
                continue;
            }

            // Use only the first ConvertedFileDescriptor to reconstruct the original file.
            // Since conversion = extension-rename only (bytes unchanged), all converted
            // versions carry the same payload; the first is sufficient.
            var primaryConvertedFile = file.ConvertedFiles.FirstOrDefault();
            if (primaryConvertedFile is null)
            {
                _logger.LogWarning("No converted files for {OriginalPath}, skipping", file.OriginalRelativePath);
                results.Add(new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath));
                continue;
            }

            var sortedChunks = primaryConvertedFile.Chunks.OrderBy(c => c.Index).ToList();
            var outputBytes = new List<byte>();
            var checksumFailed = false;

            foreach (var chunk in sortedChunks)
            {
                var chunkPath = receivedChunkPaths.FirstOrDefault(p => Path.GetFileName(p) == chunk.Name);
                if (chunkPath is null || !File.Exists(chunkPath))
                {
                    _logger.LogWarning("Chunk file not found: {ChunkName}", chunk.Name);
                    checksumFailed = true;
                    break;
                }

                var chunkBytes = await File.ReadAllBytesAsync(chunkPath);

                // Verify SHA256 checksum against the manifest
                var actualChecksum = Convert.ToHexString(SHA256.HashData(chunkBytes)).ToLowerInvariant();
                if (!string.Equals(actualChecksum, chunk.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "Checksum mismatch for chunk {ChunkName}: expected {Expected}, got {Actual}",
                        chunk.Name, chunk.Checksum, actualChecksum);
                    checksumFailed = true;
                    break;
                }

                outputBytes.AddRange(chunkBytes);
            }

            if (checksumFailed)
            {
                results.Add(new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath));
                continue;
            }

            // Write assembled bytes to the original relative path inside the assembly dir.
            // The extension is the original format — this is the "reversal" of the extension-rename conversion.
            var outputPath = Path.Combine(assemblyDir, file.OriginalRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is not null)
                Directory.CreateDirectory(outputDir);

            await File.WriteAllBytesAsync(outputPath, outputBytes.ToArray());

            results.Add(new FileResult(FileTransferStatus.Completed, file.OriginalRelativePath));
        }

        _logger.LogInformation("File assembly complete for job {JobId}: {CompletedCount} completed, {FailedCount} failed",
            blueprint.Id,
            results.Count(r => r.Status == FileTransferStatus.Completed),
            results.Count(r => r.Status != FileTransferStatus.Completed));

        return new AssembleFilesResult(results, assemblyDir);
    }
}
