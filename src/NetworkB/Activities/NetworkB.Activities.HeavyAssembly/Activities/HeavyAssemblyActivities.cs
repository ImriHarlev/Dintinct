using Microsoft.Extensions.Logging;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.HeavyAssembly.Activities;

public class HeavyAssemblyActivities
{
    private readonly ILogger<HeavyAssemblyActivities> _logger;

    public HeavyAssemblyActivities(ILogger<HeavyAssemblyActivities> logger)
    {
        _logger = logger;
    }

    [Activity]
    public async Task<IReadOnlyList<FileResult>> AssembleAndValidateAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<string> receivedChunkPaths)
    {
        _logger.LogInformation("Assembling job {JobId} with {ChunkCount} received chunks",
            blueprint.Id, receivedChunkPaths.Count);

        Directory.CreateDirectory(blueprint.TargetPath);

        var results = new List<FileResult>();

        foreach (var file in blueprint.Files)
        {
            // Determine status based on whether the file's chunks were hard-failed or unsupported
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

            foreach (var convertedFile in file.ConvertedFiles)
            {
                // Sort chunks by Index and concatenate bytes in order
                var sortedChunks = convertedFile.Chunks.OrderBy(c => c.Index).ToList();
                var outputBytes = new List<byte>();

                foreach (var chunk in sortedChunks)
                {
                    var chunkPath = receivedChunkPaths.FirstOrDefault(p => Path.GetFileName(p) == chunk.Name);
                    if (chunkPath is not null && File.Exists(chunkPath))
                    {
                        var chunkBytes = await File.ReadAllBytesAsync(chunkPath);
                        outputBytes.AddRange(chunkBytes);
                    }
                }

                // Reverse the applied conversion (mock — pseudo-code for actual reversal):
                // "DirectToProxy"  → write bytes directly to OriginalRelativePath
                // "PNG"            → reverse PNG conversion → write to OriginalRelativePath (e.g. .jpeg)
                // "DOCX_AND_PNG"   → merge DOCX + PNG bytes → reverse to OriginalRelativePath (e.g. .pdf)
                // "TXT"            → reverse TXT conversion → write to OriginalRelativePath
                var outputPath = Path.Combine(blueprint.TargetPath, file.OriginalRelativePath);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (outputDir is not null)
                    Directory.CreateDirectory(outputDir);

                await File.WriteAllBytesAsync(outputPath, outputBytes.ToArray());
            }

            results.Add(new FileResult(FileTransferStatus.Completed, file.OriginalRelativePath));
        }

        // If PackageType is an archive type (zip/rar/7z/gz), re-pack all files
        // Pseudo-code: actual archive packing would use a library like SharpCompress
        if (blueprint.PackageType is "zip" or "rar" or "7z" or "gz")
        {
            _logger.LogInformation("Package type {PackageType} — re-packing would occur here (mock)", blueprint.PackageType);
        }

        _logger.LogInformation("Assembly complete for job {JobId}: {CompletedCount} completed, {FailedCount} failed",
            blueprint.Id,
            results.Count(r => r.Status == FileTransferStatus.Completed),
            results.Count(r => r.Status != FileTransferStatus.Completed));

        return results;
    }
}
