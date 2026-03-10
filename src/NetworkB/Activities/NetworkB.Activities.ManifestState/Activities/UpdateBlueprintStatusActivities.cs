using Microsoft.Extensions.Logging;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.ManifestState.Activities;

public class UpdateBlueprintStatusActivities
{
    private readonly IAssemblyBlueprintRepository _repository;
    private readonly ILogger<UpdateBlueprintStatusActivities> _logger;

    public UpdateBlueprintStatusActivities(
        IAssemblyBlueprintRepository repository,
        ILogger<UpdateBlueprintStatusActivities> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [Activity]
    public async Task UpdateBlueprintStatusAsync(AssemblyBlueprint blueprint)
    {
        blueprint.UpdatedAt = DateTime.UtcNow;
        await _repository.UpsertAsync(blueprint);

        _logger.LogInformation("Blueprint status updated for job {JobId}: {Status}", blueprint.Id, blueprint.Status);
    }
}
