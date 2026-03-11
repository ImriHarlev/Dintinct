using Microsoft.Extensions.Logging;
using NetworkB.Activities.Reporting.Interfaces;
using NetworkB.Activities.Reporting.Services;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Temporalio.Activities;

namespace NetworkB.Activities.Reporting.Activities;

public class DispatchAnswerActivities
{
    private readonly IAnswerDispatcherFactory _dispatcherFactory;
    private readonly ILogger<DispatchAnswerActivities> _logger;

    public DispatchAnswerActivities(IAnswerDispatcherFactory dispatcherFactory, ILogger<DispatchAnswerActivities> logger)
    {
        _dispatcherFactory = dispatcherFactory;
        _logger = logger;
    }

    [Activity]
    public async Task<StatusCallbackPayload> DispatchAnswerAsync(AssemblyBlueprint blueprint, IReadOnlyList<FileResult> fileResults, JobStatus finalStatus)
    {
        _logger.LogInformation("Dispatching answer for job {JobId} via {AnswerType}", blueprint.Id, blueprint.AnswerType);

        var payload = new StatusCallbackPayload(
            CallingSystemId: blueprint.CallingSystemId,
            CallingSystemName: blueprint.CallingSystemName,
            SourcePath: blueprint.SourcePath,
            TargetPath: blueprint.TargetPath,
            TargetNetwork: blueprint.TargetNetwork,
            ExternalId: blueprint.ExternalId,
            AnswerType: blueprint.AnswerType,
            AnswerLocation: blueprint.AnswerLocation,
            JobId: Guid.NewGuid().ToString(),
            UpdateDate: DateTime.UtcNow,
            JobCount: fileResults.Count,
            OrigJobId: blueprint.Id,
            JobStatus: finalStatus);

        var dispatcher = _dispatcherFactory.GetDispatcher(blueprint.AnswerType);
        await dispatcher.DispatchAsync(payload);

        _logger.LogInformation("Answer dispatched for job {JobId} via {AnswerType}", blueprint.Id, blueprint.AnswerType);

        return payload;
    }
}
