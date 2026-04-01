using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkB.Assembly.Workflow.Activities;

public record AssemblyRuntimeConfig(WorkflowActivityConfigOptions.WorkflowConfigEntry ActivityConfig);

public class AssemblyConfigLocalActivity(IOptions<WorkflowActivityConfigOptions> activityConfig)
{
    [Activity]
    public Task<AssemblyRuntimeConfig> FetchAsync()
    {
        if (!activityConfig.Value.Configs.TryGetValue("assembly-workflow", out var entry))
            throw new InvalidOperationException(
                "No activity configuration found for 'assembly-workflow'. " +
                "Ensure an entry exists under 'WorkflowActivityConfig:Configs' in appsettings.json.");

        return Task.FromResult(new AssemblyRuntimeConfig(ActivityConfig: entry));
    }
}
