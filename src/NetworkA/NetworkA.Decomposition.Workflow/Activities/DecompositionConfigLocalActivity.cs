using Microsoft.Extensions.Options;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Decomposition.Workflow.Activities;

public record DecompositionRuntimeConfig(
    WorkflowActivityConfigOptions.WorkflowConfigEntry ActivityConfig,
    List<ProxyConfiguration> ProxyRules);

public class DecompositionConfigLocalActivity(
    IOptions<WorkflowActivityConfigOptions> activityConfig,
    IOptions<ProxyConfigOptions> proxyConfig)
{
    [Activity]
    public Task<DecompositionRuntimeConfig> FetchAsync()
    {
        if (!activityConfig.Value.Configs.TryGetValue("decomposition-workflow", out var entry))
            throw new InvalidOperationException(
                "No activity configuration found for 'decomposition-workflow'. " +
                "Ensure an entry exists under 'WorkflowActivityConfig:Configs' in appsettings.json.");

        return Task.FromResult(new DecompositionRuntimeConfig(
            ActivityConfig: entry,
            ProxyRules: proxyConfig.Value.Configurations));
    }
}
