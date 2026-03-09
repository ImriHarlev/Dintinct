using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.JobSetup.Activities;

public class JobSetupActivities
{
    private readonly IJobRepository _jobRepository;
    private readonly IProxyConfigCache _cache;
    private readonly RetryPolicyOptions _retryOptions;
    private readonly ILogger<JobSetupActivities> _logger;

    public JobSetupActivities(
        IJobRepository jobRepository,
        IProxyConfigCache cache,
        IOptions<RetryPolicyOptions> retryOptions,
        ILogger<JobSetupActivities> logger)
    {
        _jobRepository = jobRepository;
        _cache = cache;
        _retryOptions = retryOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task<WorkflowConfiguration> FetchConfigurationAsync(string jobId)
    {
        _logger.LogInformation("Fetching configuration for job {JobId}", jobId);

        var job = await _jobRepository.FindByIdAsync(jobId);
        var sourcePath = job?.OriginalRequest.SourcePath ?? string.Empty;
        var targetPath = job?.OriginalRequest.TargetPath ?? string.Empty;

        var proxyRules = await _cache.GetAllAsync();
        _logger.LogInformation("Loaded {RuleCount} proxy rules for job {JobId}", proxyRules.Count, jobId);

        return new WorkflowConfiguration(
            JobId: jobId,
            SourcePath: sourcePath,
            TargetPath: targetPath,
            MaxRetryCount: _retryOptions.MaxRetryCount,
            ProxyRules: proxyRules);
    }
}
