using Microsoft.Extensions.Logging;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.ManifestState.Activities;

public class UpdateBlueprintStatusActivities
{
    private readonly ILogger<UpdateBlueprintStatusActivities> _logger;

    public UpdateBlueprintStatusActivities(ILogger<UpdateBlueprintStatusActivities> logger)
    {
        _logger = logger;
    }

    [Activity]
    public Task UpdateBlueprintStatusAsync(AssemblyBlueprint blueprint)
    {
        _logger.LogInformation("Blueprint status for job {JobId}: {Status}", blueprint.Id, blueprint.Status);
        return Task.CompletedTask;
    }
}
