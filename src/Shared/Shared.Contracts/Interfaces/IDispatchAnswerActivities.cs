using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;

namespace Shared.Contracts.Interfaces;

public interface IDispatchAnswerActivities
{
    Task<StatusCallbackPayload> DispatchAnswerAsync(AssemblyBlueprint blueprint, IReadOnlyList<FileResult> fileResults, JobStatus finalStatus);
}
