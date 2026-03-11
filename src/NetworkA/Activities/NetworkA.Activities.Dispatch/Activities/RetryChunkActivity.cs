using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.Dispatch.Activities;

public class RetryChunkActivity
{
    private readonly IJobRepository _jobRepository;
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<RetryChunkActivity> _logger;

    public RetryChunkActivity(
        IJobRepository jobRepository,
        IOptions<OutboxOptions> outboxOptions,
        ILogger<RetryChunkActivity> logger)
    {
        _jobRepository = jobRepository;
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task RetryChunkAsync(string jobId, string chunkName)
    {
        _logger.LogInformation("Retrying chunk {ChunkName} for job {JobId}", chunkName, jobId);

        var outboxPath = ResolveOutboxPath(chunkName);
        var chunkPath = Path.Combine(outboxPath, chunkName);

        if (!File.Exists(chunkPath))
        {
            _logger.LogError("Cannot retry chunk {ChunkName}: file not found at {ChunkPath}", chunkName, chunkPath);
            return;
        }

        // Touch the existing chunk file to re-trigger the proxy file-system watcher
        File.SetLastWriteTimeUtc(chunkPath, DateTime.UtcNow);

        await _jobRepository.IncrementChunkRetryCountAsync(jobId, chunkName);

        _logger.LogInformation("Chunk {ChunkName} touched in outbox and retry counter incremented for job {JobId}",
            chunkName, jobId);
    }

    private string ResolveOutboxPath(string chunkName) =>
        chunkName.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase)
            ? _outboxOptions.ManifestOutboxPath
            : _outboxOptions.DataOutboxPath;
}
