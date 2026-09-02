using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkA.FileProcessing.Converters;
using NetworkA.FileProcessing.Splitters;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.HeavyProcessing.Activities;

public class DecomposeAndSplitActivities
{
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<DecomposeAndSplitActivities> _logger;
    private readonly FileSplitterFactory _splitterFactory;
    private readonly FileConverterFactory _converterFactory;

    public DecomposeAndSplitActivities(
        IOptions<OutboxOptions> outboxOptions,
        ILogger<DecomposeAndSplitActivities> logger,
        FileSplitterFactory splitterFactory,
        FileConverterFactory converterFactory)
    {
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
        _splitterFactory = splitterFactory;
        _converterFactory = converterFactory;
    }

    [Activity]
    public async Task<SplitResult> DecomposeAndSplitAsync(PreparedSource prepared, WorkflowConfiguration config)
    {
        _logger.LogInformation("Decomposing job {JobId} with {FileCount} files", config.JobId, prepared.SourceFiles.Count);

        Directory.CreateDirectory(_outboxOptions.DataOutboxPath);

        var proxyRulesByFormat = config.ProxyRules
            .ToDictionary(r => r.SourceFormat.ToLowerInvariant(), r => r);

        var sourceFiles = prepared.SourceFiles.ToList();
        var fileDescriptors = new FileDescriptor?[sourceFiles.Count];

        // Atomic counter: each parallel task claims a contiguous range of chunk indices.
        long[] chunkCounter = [0L];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, sourceFiles.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ActivityExecutionContext.Current.CancellationToken
            },
            async (i, ct) =>
            {
                var filePath = sourceFiles[i];
                ActivityExecutionContext.Current.Heartbeat(filePath);

                try
                {
                    var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

                    if (!proxyRulesByFormat.TryGetValue(fileExt, out var rule))
                    {
                        _logger.LogWarning("No proxy rule for extension '{Ext}', writing unsupported marker for {File}", fileExt, filePath);

                        var chunkIdx = Interlocked.Increment(ref chunkCounter[0]);
                        var unsupportedRelativePath = Path.GetRelativePath(prepared.WorkDir, filePath).Replace('\\', '/');
                        var chunkName = $"{config.JobId}_chunk_{chunkIdx}.{fileExt}.UNSUPPORTED.txt";
                        var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                        var marker = System.Text.Encoding.UTF8.GetBytes($"Unsupported file type: .{fileExt}");
                        await File.WriteAllBytesAsync(chunkPath, marker, ct);

                        var checksum = Convert.ToHexString(SHA256.HashData(marker)).ToLowerInvariant();
                        fileDescriptors[i] = new FileDescriptor(
                            unsupportedRelativePath, fileExt, [],
                            [new ConvertedFileDescriptor(unsupportedRelativePath, [new ChunkDescriptor(chunkName, 1, checksum)])]);
                        return;
                    }

                    var relativePath = Path.GetRelativePath(prepared.WorkDir, filePath).Replace('\\', '/');
                    var relDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
                    var stem = Path.GetFileNameWithoutExtension(relativePath);
                    var convertedPaths = rule.RequiredConversion.Count > 0
                        ? rule.RequiredConversion
                            .Select(ext => string.IsNullOrEmpty(relDir) ? $"{stem}.{ext}" : $"{relDir}/{stem}.{ext}")
                            .ToList()
                        : (IReadOnlyList<string>)[relativePath];

                    var convertedFileDescriptors = new List<ConvertedFileDescriptor>();

                    foreach (var convertedRelativePath in convertedPaths)
                    {
                        var chunkExt = Path.GetExtension(convertedRelativePath).TrimStart('.');
                        var converter = _converterFactory.GetConverter(fileExt, chunkExt);

                        string fileToSplit = filePath;
                        string? tempConvertedPath = null;
                        byte[]? singleChunkBytes = null;

                        if (converter.CanConvert(fileExt, chunkExt))
                        {
                            var convertedBytes = await converter.ConvertAsync(
                                new ConvertRequest(filePath, fileExt, chunkExt));

                            var maxBytes = rule.FileSizeLimitMb.HasValue
                                ? (long)rule.FileSizeLimitMb.Value * 1024 * 1024
                                : long.MaxValue;

                            if (convertedBytes.Length <= maxBytes)
                            {
                                // File fits in a single chunk — bypass temp file and splitter entirely.
                                singleChunkBytes = convertedBytes;
                            }
                            else
                            {
                                // Needs splitting: write to temp file so the splitter can read by path.
                                tempConvertedPath = Path.Combine(
                                    Path.GetTempPath(), $"dintinct_{config.JobId}_{Guid.NewGuid():N}.{chunkExt}");
                                await File.WriteAllBytesAsync(tempConvertedPath, convertedBytes, ct);
                                fileToSplit = tempConvertedPath;
                            }
                        }

                        try
                        {
                            if (singleChunkBytes is not null)
                            {
                                var idx = Interlocked.Increment(ref chunkCounter[0]);
                                var chunkName = $"{config.JobId}_chunk_{idx}.{chunkExt}";
                                var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                                await File.WriteAllBytesAsync(chunkPath, singleChunkBytes, ct);
                                var checksum = Convert.ToHexString(SHA256.HashData(singleChunkBytes)).ToLowerInvariant();
                                convertedFileDescriptors.Add(
                                    new ConvertedFileDescriptor(convertedRelativePath, [new ChunkDescriptor(chunkName, 1, checksum)]));
                            }
                            else
                            {
                                var splitter = _splitterFactory.GetSplitter(chunkExt);
                                var splitChunks = await splitter.SplitAsync(new SplitRequest(fileToSplit, rule.FileSizeLimitMb));

                                // Atomically claim a contiguous block of chunk indices for this conversion.
                                var baseIdx = Interlocked.Add(ref chunkCounter[0], splitChunks.Count) - splitChunks.Count + 1;
                                var chunks = new List<ChunkDescriptor>(splitChunks.Count);

                                for (var j = 0; j < splitChunks.Count; j++)
                                {
                                    var splitChunk = splitChunks[j];
                                    var chunkName = $"{config.JobId}_chunk_{baseIdx + j}.{chunkExt}";
                                    var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                                    await File.WriteAllBytesAsync(chunkPath, splitChunk, ct);
                                    var checksum = Convert.ToHexString(SHA256.HashData(splitChunk)).ToLowerInvariant();
                                    chunks.Add(new ChunkDescriptor(chunkName, j + 1, checksum));
                                }

                                convertedFileDescriptors.Add(new ConvertedFileDescriptor(convertedRelativePath, chunks));
                            }
                        }
                        finally
                        {
                            if (tempConvertedPath is not null && File.Exists(tempConvertedPath))
                                File.Delete(tempConvertedPath);
                        }
                    }

                    fileDescriptors[i] = new FileDescriptor(relativePath, fileExt, rule.RequiredConversion, convertedFileDescriptors);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Decompose/split cancelled for file {FilePath} in job {JobId}", filePath, config.JobId);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to decompose/split file {FilePath} for job {JobId}", filePath, config.JobId);
                    throw;
                }
            });

        var totalChunks = (int)Interlocked.Read(ref chunkCounter[0]);
        _logger.LogInformation("Job {JobId}: wrote {TotalChunks} chunks to outbox", config.JobId, totalChunks);

        DeleteOwnedWorkDir(prepared.WorkDir, config.JobId);

        return new SplitResult(
            PackageType: prepared.PackageType,
            OriginalPackageName: prepared.OriginalPackageName,
            TotalChunks: totalChunks,
            Files: fileDescriptors.OfType<FileDescriptor>().ToList(),
            NestedArchives: prepared.NestedArchives);
    }

    // PrepareSource copies or extracts package sources into %TEMP%/dintinct_{jobId}, but for a
    // single-file source it sets WorkDir to that file's real directory instead. Only ever delete
    // the temp directory this job owns - never the caller's source folder.
    private void DeleteOwnedWorkDir(string workDir, string jobId)
    {
        var owned = Path.Combine(Path.GetTempPath(), $"dintinct_{jobId}");

        if (!IsSamePath(workDir, owned))
        {
            _logger.LogDebug("Work dir {WorkDir} is not a temp dir owned by job {JobId}, leaving it in place", workDir, jobId);
            return;
        }

        try
        {
            if (Directory.Exists(owned))
            {
                Directory.Delete(owned, recursive: true);
                _logger.LogInformation("Deleted temp work dir {WorkDir} for job {JobId}", owned, jobId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a completed split over - the worker's startup sweep collects it.
            _logger.LogWarning(ex, "Could not delete temp work dir {WorkDir} for job {JobId}", owned, jobId);
        }
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
