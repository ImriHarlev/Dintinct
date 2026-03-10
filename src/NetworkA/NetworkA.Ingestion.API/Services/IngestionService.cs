using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Temporalio.Client;

namespace NetworkA.Ingestion.API.Services;

public class IngestionService : IIngestionService
{
    private readonly IJobRepository _jobRepository;
    private readonly ITemporalClient _temporalClient;

    public IngestionService(IJobRepository jobRepository, ITemporalClient temporalClient)
    {
        _jobRepository = jobRepository;
        _temporalClient = temporalClient;
    }

    public async Task<string> StartJobAsync(IngestionRequestPayload request, CancellationToken ct = default)
    {
        var jobId = Guid.NewGuid().ToString();
        var workflowId = $"decomposition-{jobId}";

        var job = new Job
        {
            Id = jobId,
            ExternalId = request.ExternalId,
            OriginalRequest = request,
            Status = "Processing",
            TemporalWorkflowId = workflowId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _jobRepository.UpsertAsync(job, ct);

        await _temporalClient.StartWorkflowAsync(
            "DecompositionWorkflow",
            new object[] { request },
            new WorkflowOptions(workflowId, "decomposition-workflow"));

        return jobId;
    }
}
