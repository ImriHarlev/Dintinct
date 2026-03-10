using Microsoft.Extensions.Logging;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.Reporting.Activities;

public class WriteCsvReportActivities
{
    private readonly ICsvReportWriter _csvReportWriter;
    private readonly ILogger<WriteCsvReportActivities> _logger;

    public WriteCsvReportActivities(ICsvReportWriter csvReportWriter, ILogger<WriteCsvReportActivities> logger)
    {
        _csvReportWriter = csvReportWriter;
        _logger = logger;
    }

    [Activity]
    public async Task WriteCsvReportAsync(AssemblyBlueprint blueprint, IReadOnlyList<FileResult> fileResults)
    {
        _logger.LogInformation("Writing CSV report for job {JobId}", blueprint.Id);

        var reportPath = Path.Combine(blueprint.TargetPath, $"{blueprint.Id}_report.csv");
        await _csvReportWriter.WriteAsync(reportPath, fileResults);

        _logger.LogInformation("CSV report written for job {JobId} at {ReportPath}", blueprint.Id, reportPath);
    }
}
