namespace Shared.Contracts.Models;

public record WorkflowConfiguration(
    string JobId,
    string SourcePath,
    string TargetPath,
    IReadOnlyList<ProxyConfiguration> ProxyRules);
