using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NetworkB.FileAssembly.Assemblers;
using NetworkB.FileAssembly.Converters;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.HeavyAssembly.Activities;

public class AssembleFilesActivities
{
    private readonly FileAssemblerFactory _assemblerFactory;
    private readonly FileConverterFactory _converterFactory;
    private readonly ILogger<AssembleFilesActivities> _logger;

    public AssembleFilesActivities(
        FileAssemblerFactory assemblerFactory,
        FileConverterFactory converterFactory,
        ILogger<AssembleFilesActivities> logger)
    {
        _assemblerFactory = assemblerFactory;
        _converterFactory = converterFactory;
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

        // Build O(1) lookup once rather than scanning the list for every chunk.
        var chunkPathByName = receivedChunkPaths.ToDictionary(p => Path.GetFileName(p)!);

        var resultsArray = new FileResult?[blueprint.Files.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, blueprint.Files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ActivityExecutionContext.Current.CancellationToken
            },
            async (i, ct) =>
            {
                var file = blueprint.Files[i];
                ActivityExecutionContext.Current.Heartbeat(file.OriginalRelativePath);

                try
                {
                    var allChunkNames = file.ConvertedFiles
                        .SelectMany(cf => cf.Chunks)
                        .Select(c => c.Name)
                        .ToHashSet();

                    if (allChunkNames.Any(c => blueprint.HardFailedChunkNames.Contains(c)))
                    {
                        resultsArray[i] = new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath);
                        return;
                    }

                    if (allChunkNames.Any(c => blueprint.UnsupportedChunkNames.Contains(c)))
                    {
                        resultsArray[i] = new FileResult(FileTransferStatus.NotSupported, file.OriginalRelativePath);
                        return;
                    }

                    var primaryConvertedFile = file.ConvertedFiles.FirstOrDefault();
                    if (primaryConvertedFile is null)
                    {
                        _logger.LogWarning("No converted files for {OriginalPath}, skipping", file.OriginalRelativePath);
                        resultsArray[i] = new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath);
                        return;
                    }

                    var sortedChunks = primaryConvertedFile.Chunks.OrderBy(c => c.Index).ToList();
                    var assembledChunks = new List<AssemblyChunk>();
                    var checksumFailed = false;

                    foreach (var chunk in sortedChunks)
                    {
                        if (!chunkPathByName.TryGetValue(chunk.Name, out var chunkPath) || !File.Exists(chunkPath))
                        {
                            _logger.LogWarning("Chunk file not found: {ChunkName}", chunk.Name);
                            checksumFailed = true;
                            break;
                        }

                        var chunkBytes = await File.ReadAllBytesAsync(chunkPath, ct);

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
                        resultsArray[i] = new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath);
                        return;
                    }

                    var convertedExt = Path.GetExtension(primaryConvertedFile.ConvertedRelativePath).TrimStart('.');
                    var tempOutputPath = Path.Combine(
                        assemblyDir,
                        primaryConvertedFile.ConvertedRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(tempOutputPath)!);

                    var assembler = _assemblerFactory.GetAssembler(convertedExt);
                    await assembler.AssembleAsync(new AssemblyRequest(convertedExt, tempOutputPath, assembledChunks));

                    var finalOutputPath = Path.Combine(
                        assemblyDir,
                        file.OriginalRelativePath.Replace('/', Path.DirectorySeparatorChar));

                    var needsConversion = !string.Equals(convertedExt, file.OriginalFormat,
                        StringComparison.OrdinalIgnoreCase);

                    if (needsConversion)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(finalOutputPath)!);
                        var converter = _converterFactory.GetConverter(convertedExt, file.OriginalFormat);
                        if (converter.CanConvert(convertedExt, file.OriginalFormat))
                        {
                            // Wrap in try/finally so the assembled temp file is always cleaned up.
                            // Without this, a ConvertAsync exception leaves the DOCX on disk, and
                            // RepackAndFinalizeActivities copies it to the output — making it look
                            // like the file "remained as <intermediate format>" even though the
                            // activity itself reported Failed.
                            try
                            {
                                var convertedBytes = await converter.ConvertAsync(
                                    new ConvertRequest(tempOutputPath, convertedExt, file.OriginalFormat));
                                await File.WriteAllBytesAsync(finalOutputPath, convertedBytes, ct);
                            }
                            finally
                            {
                                if (File.Exists(tempOutputPath) && tempOutputPath != finalOutputPath)
                                    File.Delete(tempOutputPath);
                            }
                        }
                        else
                        {
                            var fallbackPath = Path.ChangeExtension(finalOutputPath, "." + convertedExt.ToLowerInvariant());
                            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
                            File.Move(tempOutputPath, fallbackPath, overwrite: true);
                            _logger.LogWarning(
                            "No back-converter found for {ConvertedExt} → {OriginalFormat}; " +
                                "keeping intermediate file as {FallbackPath}",
                                convertedExt, file.OriginalFormat, fallbackPath);
                            resultsArray[i] = new FileResult(FileTransferStatus.Completed, file.OriginalRelativePath);
                            return;
                        }
                    }

                    resultsArray[i] = new FileResult(FileTransferStatus.Completed, file.OriginalRelativePath);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Assembly cancelled for file {FilePath} in job {JobId}", file.OriginalRelativePath, blueprint.Id);
                    resultsArray[i] = new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to assemble file {FilePath} for job {JobId}",
                        file.OriginalRelativePath, blueprint.Id);
                    resultsArray[i] = new FileResult(FileTransferStatus.Failed, file.OriginalRelativePath);
                }
            });

        var results = resultsArray.OfType<FileResult>().ToList();

        _logger.LogInformation("File assembly complete for job {JobId}: {CompletedCount} completed, {FailedCount} failed",
            blueprint.Id,
            results.Count(r => r.Status == FileTransferStatus.Completed),
            results.Count(r => r.Status != FileTransferStatus.Completed));

        return new AssembleFilesResult(results);
    }
}
