using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.HeavyProcessing.Activities;

public class HeavyProcessingActivities
{
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<HeavyProcessingActivities> _logger;

    public HeavyProcessingActivities(
        IOptions<OutboxOptions> outboxOptions,
        ILogger<HeavyProcessingActivities> logger)
    {
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task<DecompositionMetadata> DecomposeAndSplitAsync(WorkflowConfiguration config)
    {
        _logger.LogInformation("Decomposing job {JobId} from source {SourcePath}", config.JobId, config.SourcePath);

        Directory.CreateDirectory(_outboxOptions.DataOutboxPath);

        var proxyRulesByFormat = config.ProxyRules
            .ToDictionary(r => r.SourceFormat.ToLowerInvariant(), r => r);

        string workDir;
        string packageType;
        string originalPackageName;

        var ext = Path.GetExtension(config.SourcePath).TrimStart('.').ToLowerInvariant();

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
            workDir = config.SourcePath;
        }
        else if (File.Exists(config.SourcePath))
        {
            packageType = ext;
            originalPackageName = Path.GetFileName(config.SourcePath);
            workDir = Path.GetDirectoryName(config.SourcePath)!;
        }
        else
        {
            throw new FileNotFoundException($"SourcePath not found: {config.SourcePath}");
        }

        var sourceFiles = Directory
            .EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
            .ToList();

        _logger.LogInformation("Found {FileCount} files in source for job {JobId}", sourceFiles.Count, config.JobId);

        var files = new List<FileDescriptor>();
        var chunkIndex = 1;

        foreach (var filePath in sourceFiles)
        {
            var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (!proxyRulesByFormat.TryGetValue(fileExt, out var rule))
            {
                _logger.LogWarning("No proxy rule for extension '{Ext}', skipping file {File}", fileExt, filePath);
                continue;
            }

            // Archive-type rules mean the file should have been extracted already; skip
            if (rule.RequiredConversion.Any(c => c.StartsWith("Extract ", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Skipping nested archive {File}", filePath);
                continue;
            }

            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var relativePath = Path.GetRelativePath(workDir, filePath).Replace('\\', '/');

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
                var chunkSizeBytes = rule.FileSizeLimitMb.HasValue
                    ? rule.FileSizeLimitMb.Value * 1024 * 1024
                    : fileBytes.Length;

                var chunkCount = (int)Math.Ceiling((double)fileBytes.Length / chunkSizeBytes);
                if (chunkCount < 1) chunkCount = 1;

                var chunks = new List<ChunkDescriptor>();

                for (var i = 0; i < chunkCount; i++)
                {
                    var offset = i * chunkSizeBytes;
                    var length = Math.Min(chunkSizeBytes, fileBytes.Length - offset);
                    var slice = fileBytes.AsSpan(offset, length).ToArray();

                    var chunkExt = Path.GetExtension(convertedRelativePath).TrimStart('.');
                    var chunkName = $"{config.JobId}_chunk_{chunkIndex}.{chunkExt}";
                    var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
                    await File.WriteAllBytesAsync(chunkPath, slice);

                    var checksum = Convert.ToHexString(SHA256.HashData(slice)).ToLowerInvariant();
                    chunks.Add(new ChunkDescriptor(chunkName, i + 1, checksum));
                    chunkIndex++;
                }

                convertedFileDescriptors.Add(new ConvertedFileDescriptor(convertedRelativePath, chunks));
            }

            files.Add(new FileDescriptor(relativePath, fileExt, rule.RequiredConversion, convertedFileDescriptors));
        }

        var totalChunks = chunkIndex - 1;
        _logger.LogInformation("Job {JobId}: wrote {TotalChunks} chunks to outbox", config.JobId, totalChunks);

        // AnswerType, AnswerLocation, and CallingSystem fields are enriched by the workflow
        // before passing to ManifestActivities
        return new DecompositionMetadata(
            JobId: config.JobId,
            PackageType: packageType,
            OriginalPackageName: originalPackageName,
            SourcePath: config.SourcePath,
            TargetPath: config.TargetPath,
            TargetNetwork: string.Empty,
            CallingSystemId: string.Empty,
            CallingSystemName: string.Empty,
            ExternalId: string.Empty,
            TotalChunks: totalChunks,
            AnswerType: default,
            AnswerLocation: null,
            Files: files);
    }

}
