using Shared.Contracts.Models;

namespace Shared.Infrastructure.Repositories;

public interface IWorkflowActivityConfigRepository
{
    Task<WorkflowActivityConfig?> GetByWorkflowKeyAsync(string workflowKey, CancellationToken ct = default);
}
