using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NetworkB.Activities.HeavyAssembly.Assemblers;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.HeavyAssembly.Activities;

public class AssembleFilesActivities
{
    private readonly FileAssemblerFactory _assemblerFactory;
    private readonly ILogger<AssembleFilesActivities> _logger;

    public AssembleFilesActivities(FileAssemblerFactory assemblerFactory, ILogger<AssembleFilesActivities> logger)
    {
        _assemblerFactory = assemblerFactory;
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
            var assembledChunks = new List<AssemblyChunk>();
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

                assembledChunks.Add(new AssemblyChunk(chunk.Name, chunkPath, chunkBytes));
            }

            if (checksumFailed)
            {
                results.Add(new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath));
                continue;
            }

            var outputPath = Path.Combine(assemblyDir, file.OriginalRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is not null)
            {
                Directory.CreateDirectory(outputDir);
            }

            var assembler = _assemblerFactory.GetAssembler(file.OriginalFormat);
            await assembler.AssembleAsync(new AssemblyRequest(file.OriginalFormat, outputPath, assembledChunks));

            results.Add(new FileResult(FileTransferStatus.Completed, file.OriginalRelativePath));
        }

        _logger.LogInformation("File assembly complete for job {JobId}: {CompletedCount} completed, {FailedCount} failed",
            blueprint.Id,
            results.Count(r => r.Status == FileTransferStatus.Completed),
            results.Count(r => r.Status != FileTransferStatus.Completed));

        return new AssembleFilesResult(results, assemblyDir);
    }
}
