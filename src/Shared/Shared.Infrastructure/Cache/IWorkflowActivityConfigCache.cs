using Shared.Contracts.Models;

namespace Shared.Infrastructure.Cache;

public interface IWorkflowActivityConfigCache
{
    Task<WorkflowActivityConfig?> GetAsync(string workflowKey, CancellationToken ct = default);
    Task InvalidateAsync(string workflowKey, CancellationToken ct = default);
}
