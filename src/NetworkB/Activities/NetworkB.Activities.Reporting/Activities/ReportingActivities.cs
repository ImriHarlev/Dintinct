using Microsoft.Extensions.Logging;
using NetworkB.Activities.Reporting.Services;
using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Temporalio.Activities;

namespace NetworkB.Activities.Reporting.Activities;

public class ReportingActivities
{
    private readonly ICsvReportWriter _csvReportWriter;
    private readonly IAnswerDispatcherFactory _dispatcherFactory;
    private readonly INetworkAClient _networkAClient;
    private readonly ILogger<ReportingActivities> _logger;

    public ReportingActivities(
        ICsvReportWriter csvReportWriter,
        IAnswerDispatcherFactory dispatcherFactory,
        INetworkAClient networkAClient,
        ILogger<ReportingActivities> logger)
    {
        _csvReportWriter = csvReportWriter;
        _dispatcherFactory = dispatcherFactory;
        _networkAClient = networkAClient;
        _logger = logger;
    }

    [Activity]
    public async Task GenerateAndDispatchReportAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<FileResult> fileResults,
        JobStatus finalStatus)
    {
        _logger.LogInformation("Generating report for job {JobId} with status {Status}", blueprint.Id, finalStatus);

        var reportPath = Path.Combine(blueprint.TargetPath, $"{blueprint.Id}_report.csv");
        await _csvReportWriter.WriteAsync(reportPath, fileResults);

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

        await _networkAClient.SendFinalStatusAsync(payload);

        _logger.LogInformation("Report dispatched for job {JobId} via {AnswerType}", blueprint.Id, blueprint.AnswerType);
    }
}
