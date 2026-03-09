using Shared.Contracts.Interfaces;
using Shared.Contracts.Payloads;
using Temporalio.Client;

namespace NetworkA.Callback.Receiver.Services;

public class CallbackService : ICallbackService
{
    private readonly IJobRepository _jobRepository;
    private readonly ITemporalClient _temporalClient;
    private readonly ILogger<CallbackService> _logger;

    public CallbackService(
        IJobRepository jobRepository,
        ITemporalClient temporalClient,
        ILogger<CallbackService> logger)
    {
        _jobRepository = jobRepository;
        _temporalClient = temporalClient;
        _logger = logger;
    }

    public async Task HandleFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default)
    {
        var job = await _jobRepository.FindByIdAsync(payload.OrigJobId, ct);
        if (job is null)
        {
            _logger.LogWarning("Job not found for OrigJobId {OrigJobId}", payload.OrigJobId);
            return;
        }

        var handle = _temporalClient.GetWorkflowHandle(job.TemporalWorkflowId);
        await handle.SignalAsync("FinalStatusReceived", new object[] { payload });

        _logger.LogInformation("Signalled workflow {WorkflowId} with final status {Status}",
            job.TemporalWorkflowId, payload.JobStatus);
    }

    public async Task HandleChunkRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default)
    {
        var job = await _jobRepository.FindByIdAsync(origJobId, ct);
        if (job is null)
        {
            _logger.LogWarning("Job not found for OrigJobId {OrigJobId}", origJobId);
            return;
        }

        var handle = _temporalClient.GetWorkflowHandle(job.TemporalWorkflowId);
        await handle.SignalAsync("ChunkRetryRequested", new object[] { chunkName });

        _logger.LogInformation("Signalled workflow {WorkflowId} for chunk retry: {ChunkName}",
            job.TemporalWorkflowId, chunkName);
    }
}
