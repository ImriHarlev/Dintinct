using Shared.Contracts.Models;
using Shared.Infrastructure.Cache;
using Temporalio.Activities;

namespace Shared.Infrastructure.Activities;

public class WorkflowActivityConfigLocalActivity
{
    private readonly IWorkflowActivityConfigCache _cache;

    public WorkflowActivityConfigLocalActivity(IWorkflowActivityConfigCache cache)
    {
        _cache = cache;
    }

    [Activity]
    public async Task<WorkflowActivityConfig> FetchAsync(string workflowKey)
    {
        var config = await _cache.GetAsync(workflowKey);

        if (config is null)
            throw new InvalidOperationException(
                $"No activity configuration found in MongoDB for workflow key '{workflowKey}'. " +
                "Insert a document into the 'workflow_activity_configs' collection before starting workflows.");

        return config;
    }
}
