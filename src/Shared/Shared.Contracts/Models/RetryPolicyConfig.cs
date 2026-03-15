namespace Shared.Contracts.Models;

public record RetryPolicyConfig(
    int InitialIntervalSeconds,
    double BackoffCoefficient,
    int MaximumIntervalSeconds,
    int MaximumAttempts,
    IReadOnlyList<string> NonRetryableErrorTypes);
