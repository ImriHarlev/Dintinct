using NetworkA.Callback.Receiver.Interfaces;
using Shared.Contracts.Payloads;
using Temporalio.Client;

namespace NetworkA.Callback.Receiver.Services;

public class CallbackService : ICallbackService
{
    private readonly ITemporalClient _temporalClient;
    private readonly ILogger<CallbackService> _logger;

    public CallbackService(ITemporalClient temporalClient, ILogger<CallbackService> logger)
    {
        _temporalClient = temporalClient;
        _logger = logger;
    }

    public async Task HandleFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default)
    {
        var workflowId = $"decomposition-{payload.OrigJobId}";
        var handle = _temporalClient.GetWorkflowHandle(workflowId);
        await handle.SignalAsync("FinalStatusReceived", [payload]);

        _logger.LogInformation("Signalled workflow {WorkflowId} with final status {Status}",
            workflowId, payload.JobStatus);
    }

    public async Task HandleChunkRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default)
    {
        var workflowId = $"decomposition-{origJobId}";
        var handle = _temporalClient.GetWorkflowHandle(workflowId);
        await handle.SignalAsync("ChunkRetryRequested", [chunkName]);

        _logger.LogInformation("Signalled workflow {WorkflowId} for chunk retry: {ChunkName}",
            workflowId, chunkName);
    }
}
