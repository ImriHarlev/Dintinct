namespace Shared.Contracts.Models;

public record WorkflowConfiguration(
    string JobId,
    string SourcePath,
    string TargetPath,
    int MockChunkCount,
    int MaxRetryCount,
    IReadOnlyList<ProxyConfiguration> ProxyRules);
