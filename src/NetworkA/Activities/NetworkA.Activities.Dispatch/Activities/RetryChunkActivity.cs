using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.Dispatch.Activities;

public class RetryChunkActivity
{
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<RetryChunkActivity> _logger;

    public RetryChunkActivity(IOptions<OutboxOptions> outboxOptions, ILogger<RetryChunkActivity> logger)
    {
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

        File.SetLastWriteTimeUtc(chunkPath, DateTime.UtcNow);

        _logger.LogInformation("Chunk {ChunkName} touched in outbox for retry, job {JobId}", chunkName, jobId);
    }

    private string ResolveOutboxPath(string chunkName) =>
        chunkName.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase)
            ? _outboxOptions.ManifestOutboxPath
            : _outboxOptions.DataOutboxPath;
}
