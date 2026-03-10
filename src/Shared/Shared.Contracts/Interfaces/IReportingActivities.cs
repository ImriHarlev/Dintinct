using Shared.Contracts.Enums;
using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IReportingActivities
{
    Task GenerateAndDispatchReportAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<FileResult> fileResults,
        JobStatus finalStatus);
}
