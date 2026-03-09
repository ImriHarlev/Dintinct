using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IHeavyProcessingActivities
{
    Task<DecompositionMetadata> DecomposeAndSplitAsync(WorkflowConfiguration config);
}
