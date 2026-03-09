using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IManifestStateActivities
{
    Task<AssemblyBlueprint> ParseAndPersistManifestAsync(string manifestFilePath);
}
