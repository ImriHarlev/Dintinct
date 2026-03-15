namespace Shared.Contracts.Models;

public record WorkflowActivityConfig(
    string WorkflowKey,
    IReadOnlyDictionary<string, ActivityTimeoutConfig> Activities);
