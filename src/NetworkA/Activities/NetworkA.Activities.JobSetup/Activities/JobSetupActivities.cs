using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.JobSetup.Activities;

public class JobSetupActivities
{
    private readonly ProxyConfigOptions _proxyConfig;
    private readonly RetryPolicyOptions _retryOptions;
    private readonly ILogger<JobSetupActivities> _logger;

    public JobSetupActivities(
        IOptions<ProxyConfigOptions> proxyConfig,
        IOptions<RetryPolicyOptions> retryOptions,
        ILogger<JobSetupActivities> logger)
    {
        _proxyConfig = proxyConfig.Value;
        _retryOptions = retryOptions.Value;
        _logger = logger;
    }

    [Activity]
    public Task<WorkflowConfiguration> FetchConfigurationAsync(string jobId, string sourcePath, string targetPath)
    {
        _logger.LogInformation("Fetching configuration for job {JobId}", jobId);

        var proxyRules = _proxyConfig.Configurations;
        _logger.LogInformation("Loaded {RuleCount} proxy rules for job {JobId}", proxyRules.Count, jobId);

        return Task.FromResult(new WorkflowConfiguration(
            JobId: jobId,
            SourcePath: sourcePath,
            TargetPath: targetPath,
            MaxRetryCount: _retryOptions.MaxRetryCount,
            ProxyRules: proxyRules));
    }
}
