using Microsoft.Extensions.Options;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace Shared.Infrastructure.Activities;

public class WorkflowActivityConfigLocalActivity
{
    private readonly WorkflowActivityConfigOptions _options;

    public WorkflowActivityConfigLocalActivity(IOptions<WorkflowActivityConfigOptions> options)
    {
        _options = options.Value;
    }

    [Activity]
    public Task<WorkflowActivityConfig> FetchAsync(string workflowKey)
    {
        if (!_options.Configs.TryGetValue(workflowKey, out var entry))
            throw new InvalidOperationException(
                $"No activity configuration found for workflow key '{workflowKey}'. " +
                "Add an entry under 'WorkflowActivityConfig:Configs' in appsettings.json.");

        var activities = entry.Activities.ToDictionary(
            kvp => kvp.Key,
            kvp => new ActivityTimeoutConfig(
                kvp.Value.StartToCloseMinutes,
                kvp.Value.ScheduleToCloseMinutes,
                kvp.Value.HeartbeatSeconds,
                kvp.Value.RetryPolicy is { } rp
                    ? new RetryPolicyConfig(
                        rp.InitialIntervalSeconds,
                        rp.BackoffCoefficient,
                        rp.MaximumIntervalSeconds,
                        rp.MaximumAttempts,
                        rp.NonRetryableErrorTypes)
                    : null));

        return Task.FromResult(new WorkflowActivityConfig(workflowKey, activities));
    }
}
