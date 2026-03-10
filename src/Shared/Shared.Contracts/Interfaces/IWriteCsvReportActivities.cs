using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IWriteCsvReportActivities
{
    Task WriteCsvReportAsync(AssemblyBlueprint blueprint, IReadOnlyList<FileResult> fileResults);
}
