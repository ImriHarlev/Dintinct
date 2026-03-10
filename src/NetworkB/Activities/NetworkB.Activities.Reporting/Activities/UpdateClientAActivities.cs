using Microsoft.Extensions.Logging;
using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Payloads;
using Temporalio.Activities;

namespace NetworkB.Activities.Reporting.Activities;

public class UpdateClientAActivities
{
    private readonly INetworkAClient _networkAClient;
    private readonly ILogger<UpdateClientAActivities> _logger;

    public UpdateClientAActivities(INetworkAClient networkAClient, ILogger<UpdateClientAActivities> logger)
    {
        _networkAClient = networkAClient;
        _logger = logger;
    }

    [Activity]
    public async Task UpdateClientAAsync(StatusCallbackPayload payload)
    {
        _logger.LogInformation("Updating Client A for job {OrigJobId} with status {Status}", payload.OrigJobId, payload.JobStatus);

        await _networkAClient.SendFinalStatusAsync(payload);

        _logger.LogInformation("Client A updated for job {OrigJobId}", payload.OrigJobId);
    }

    [Activity]
    public async Task NotifyManifestFailureAsync(string origJobId)
    {
        _logger.LogWarning("Manifest hard-failed for job {OrigJobId}, notifying Client A", origJobId);

        var payload = new StatusCallbackPayload(
            CallingSystemId: string.Empty,
            CallingSystemName: string.Empty,
            SourcePath: string.Empty,
            TargetPath: string.Empty,
            TargetNetwork: string.Empty,
            ExternalId: string.Empty,
            AnswerType: default,
            AnswerLocation: null,
            JobId: Guid.NewGuid().ToString(),
            UpdateDate: DateTime.UtcNow,
            JobCount: 0,
            OrigJobId: origJobId,
            JobStatus: JobStatus.Failed);

        await _networkAClient.SendFinalStatusAsync(payload);

        _logger.LogInformation("Client A notified of manifest failure for job {OrigJobId}", origJobId);
    }
}
