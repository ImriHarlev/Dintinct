using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IManifestActivities
{
    Task WriteManifestAsync(DecompositionMetadata metadata);
}
