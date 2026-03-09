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
        _logger.LogInformation("Decomposing job {JobId} with {ChunkCount} chunks per file", config.JobId, config.MockChunkCount);

        Directory.CreateDirectory(_outboxOptions.DataOutboxPath);

        var files = new List<FileDescriptor>();
        var chunkIndex = 1;

        // Mock file entries matching proxy rules
        var mockFiles = new[]
        {
            (path: "docs/report.pdf", format: "pdf", conversion: "DOCX_AND_PNG",
                convertedFiles: new[] { "docs/report.docx", "docs/report.png" }),
            (path: "images/photo.jpeg", format: "jpeg", conversion: "PNG",
                convertedFiles: new[] { "images/photo.png" }),
            (path: "data/notes.txt", format: "txt", conversion: "DirectToProxy",
                convertedFiles: new[] { "data/notes.txt" })
        };

        foreach (var mockFile in mockFiles)
        {
            var convertedFileDescriptors = new List<ConvertedFileDescriptor>();

            foreach (var convertedPath in mockFile.convertedFiles)
            {
                var chunks = new List<ChunkDescriptor>();
                for (var i = 0; i < config.MockChunkCount; i++)
                {
                    var chunkName = $"{config.JobId}_chunk_{chunkIndex}.bin";
                    var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);

                    // Write empty mock chunk file
                    await File.WriteAllBytesAsync(chunkPath, Array.Empty<byte>());

                    chunks.Add(new ChunkDescriptor(chunkName, i + 1, "N/A"));
                    chunkIndex++;
                }
                convertedFileDescriptors.Add(new ConvertedFileDescriptor(convertedPath, chunks));
            }

            files.Add(new FileDescriptor(mockFile.path, mockFile.format, mockFile.conversion, convertedFileDescriptors));
        }

        var totalChunks = chunkIndex - 1;
        _logger.LogInformation("Job {JobId}: wrote {TotalChunks} mock chunks to outbox", config.JobId, totalChunks);

        // AnswerType and AnswerLocation are set to defaults here;
        // the workflow enriches them from the original request before passing to ManifestActivities
        return new DecompositionMetadata(
            JobId: config.JobId,
            PackageType: "zip",
            OriginalPackageName: "test-package.zip",
            TargetPath: config.TargetPath,
            TotalChunks: totalChunks,
            AnswerType: default,
            AnswerLocation: null,
            Files: files);
    }
}
