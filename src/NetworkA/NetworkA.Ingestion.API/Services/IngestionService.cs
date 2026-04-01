using NetworkA.Ingestion.API.Interfaces;
using Shared.Contracts.Payloads;
using Temporalio.Client;

namespace NetworkA.Ingestion.API.Services;

public class IngestionService : IIngestionService
{
    private readonly ITemporalClient _temporalClient;

    public IngestionService(ITemporalClient temporalClient)
    {
        _temporalClient = temporalClient;
    }

    public async Task<string> StartJobAsync(IngestionRequestPayload request, CancellationToken ct = default)
    {
        var jobId = Guid.NewGuid().ToString();
        var workflowId = $"decomposition-{jobId}";

        await _temporalClient.StartWorkflowAsync(
            "DecompositionWorkflow",
            [request],
            new WorkflowOptions(workflowId, "decomposition-workflow"));

        return jobId;
    }
}
