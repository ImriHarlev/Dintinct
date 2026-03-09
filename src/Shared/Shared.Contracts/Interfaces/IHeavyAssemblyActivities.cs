using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IHeavyAssemblyActivities
{
    Task<IReadOnlyList<FileResult>> AssembleAndValidateAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<string> receivedChunkPaths);
}
