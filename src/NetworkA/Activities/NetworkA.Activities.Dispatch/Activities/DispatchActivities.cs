using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.Dispatch.Activities;

public class DispatchActivities
{
    private readonly IJobRepository _jobRepository;
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<DispatchActivities> _logger;

    public DispatchActivities(
        IJobRepository jobRepository,
        IOptions<OutboxOptions> outboxOptions,
        ILogger<DispatchActivities> logger)
    {
        _jobRepository = jobRepository;
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task RetryChunkAsync(string jobId, string chunkName)
    {
        _logger.LogInformation("Retrying chunk {ChunkName} for job {JobId}", chunkName, jobId);

        Directory.CreateDirectory(_outboxOptions.DataOutboxPath);

        // Re-write mock chunk file to Data Outbox so the Proxy picks it up again
        var chunkPath = Path.Combine(_outboxOptions.DataOutboxPath, chunkName);
        await File.WriteAllBytesAsync(chunkPath, Array.Empty<byte>());

        await _jobRepository.IncrementChunkRetryCountAsync(jobId, chunkName);

        _logger.LogInformation("Chunk {ChunkName} re-queued in outbox and retry counter incremented for job {JobId}",
            chunkName, jobId);
    }

    [Activity]
    public async Task WriteHardFailAsync(string jobId, string chunkName)
    {
        _logger.LogInformation("Writing hard-fail marker for chunk {ChunkName}, job {JobId}", chunkName, jobId);

        Directory.CreateDirectory(_outboxOptions.DataOutboxPath);

        // Write a .HARDFAIL.txt file per data-model.md §3.1 naming convention
        var hardFailFileName = $"{chunkName}.HARDFAIL.txt";
        var hardFailPath = Path.Combine(_outboxOptions.DataOutboxPath, hardFailFileName);
        await File.WriteAllTextAsync(hardFailPath, $"Hard fail for chunk {chunkName} in job {jobId}");

        _logger.LogInformation("Hard-fail marker written: {HardFailPath}", hardFailPath);
    }
}
