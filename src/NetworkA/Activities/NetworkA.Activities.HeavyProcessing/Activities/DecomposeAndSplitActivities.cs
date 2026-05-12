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

        var files = new List<FileDescriptor>();
        var chunkIndex = 1;

        foreach (var filePath in prepared.SourceFiles)
        {
            ActivityExecutionContext.Current.Heartbeat(filePath);

            var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (!proxyRulesByFormat.TryGetValue(fileExt, out var rule))
            {
                _logger.LogWarning("No proxy rule for extension '{Ext}', writing unsupported marker for {File}", fileExt, filePath);

                var unsupportedRelativePath = Path.GetRelativePath(prepared.WorkDir, filePath).Replace('\\', '/');
                var chunkName = $"{config.JobId}_chunk_{chunkIndex}.{fileExt}.UNSUPPORTED.txt";
                var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                var marker = System.Text.Encoding.UTF8.GetBytes($"Unsupported file type: .{fileExt}");
                await File.WriteAllBytesAsync(chunkPath, marker);

                var checksum = Convert.ToHexString(SHA256.HashData(marker)).ToLowerInvariant();
                files.Add(new FileDescriptor(unsupportedRelativePath, fileExt, [], [new ConvertedFileDescriptor(unsupportedRelativePath, [new ChunkDescriptor(chunkName, 1, checksum)])]));
                chunkIndex++;
                continue;
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

                // Convert the source file to the required format if a converter exists.
                // The converted bytes are written to a temp file so the splitter can read
                // from a file path (preserving the existing IFileSplitter contract).
                var converter = _converterFactory.GetConverter(fileExt, chunkExt);
                string fileToSplit = filePath;
                string? tempConvertedPath = null;
                if (converter.CanConvert(fileExt, chunkExt))
                {
                    var convertedBytes = await converter.ConvertAsync(
                        new ConvertRequest(filePath, fileExt, chunkExt));
                    tempConvertedPath = Path.Combine(
                        Path.GetTempPath(), $"dintinct_{config.JobId}_{Guid.NewGuid():N}.{chunkExt}");
                    await File.WriteAllBytesAsync(tempConvertedPath, convertedBytes);
                    fileToSplit = tempConvertedPath;
                }

                // Select the splitter by the *converted* extension so format-specific splitters
                // (e.g. ImageFileSplitter for png) are used rather than falling back to the default.
                var splitter = _splitterFactory.GetSplitter(chunkExt);

                try
                {
                    var splitChunks = await splitter.SplitAsync(new SplitRequest(fileToSplit, rule.FileSizeLimitMb));
                    var chunks = new List<ChunkDescriptor>();

                    for (var i = 0; i < splitChunks.Count; i++)
                    {
                        var splitChunk = splitChunks[i];

                        var chunkName = $"{config.JobId}_chunk_{chunkIndex}.{chunkExt}";
                        var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                        await File.WriteAllBytesAsync(chunkPath, splitChunk);

                        var checksum = Convert.ToHexString(SHA256.HashData(splitChunk)).ToLowerInvariant();
                        chunks.Add(new ChunkDescriptor(chunkName, i + 1, checksum));
                        chunkIndex++;
                    }

                    convertedFileDescriptors.Add(new ConvertedFileDescriptor(convertedRelativePath, chunks));
                }
                finally
                {
                    if (tempConvertedPath is not null && File.Exists(tempConvertedPath))
                        File.Delete(tempConvertedPath);
                }
            }

            files.Add(new FileDescriptor(relativePath, fileExt, rule.RequiredConversion, convertedFileDescriptors));
        }

        var totalChunks = chunkIndex - 1;
        _logger.LogInformation("Job {JobId}: wrote {TotalChunks} chunks to outbox", config.JobId, totalChunks);

        return new SplitResult(
            PackageType: prepared.PackageType,
            OriginalPackageName: prepared.OriginalPackageName,
            TotalChunks: totalChunks,
            Files: files,
            NestedArchives: prepared.NestedArchives);
    }
}
