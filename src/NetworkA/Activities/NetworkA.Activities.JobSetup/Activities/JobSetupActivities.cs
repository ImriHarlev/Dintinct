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
    private readonly MockOptions _mockOptions;
    private readonly RetryPolicyOptions _retryOptions;
    private readonly ILogger<JobSetupActivities> _logger;

    public JobSetupActivities(
        IJobRepository jobRepository,
        IProxyConfigCache cache,
        IOptions<MockOptions> mockOptions,
        IOptions<RetryPolicyOptions> retryOptions,
        ILogger<JobSetupActivities> logger)
    {
        _jobRepository = jobRepository;
        _cache = cache;
        _mockOptions = mockOptions.Value;
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

        var proxyRules = new List<ProxyConfiguration>
        {
            new() { Id = "1", SourceFormat = "pdf", RequiredConversion = "DOCX_AND_PNG" },
            new() { Id = "2", SourceFormat = "jpeg", RequiredConversion = "PNG" },
            new() { Id = "3", SourceFormat = "txt", RequiredConversion = "DirectToProxy" }
        };

        await _cache.GetAsync("pdf");
        _logger.LogInformation("Redis and MongoDB connectivity confirmed for job {JobId}", jobId);

        return new WorkflowConfiguration(
            JobId: jobId,
            SourcePath: sourcePath,
            TargetPath: targetPath,
            MockChunkCount: _mockOptions.MockChunkCount,
            MaxRetryCount: _retryOptions.MaxRetryCount,
            ProxyRules: proxyRules);
    }
}
