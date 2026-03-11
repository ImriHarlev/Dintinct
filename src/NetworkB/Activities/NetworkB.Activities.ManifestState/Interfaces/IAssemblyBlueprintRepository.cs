using Shared.Contracts.Models;

namespace NetworkB.Activities.ManifestState.Interfaces;

public interface IAssemblyBlueprintRepository
{
    Task<AssemblyBlueprint?> FindByJobIdAsync(string jobId, CancellationToken ct = default);
    Task UpsertAsync(AssemblyBlueprint blueprint, CancellationToken ct = default);
}
