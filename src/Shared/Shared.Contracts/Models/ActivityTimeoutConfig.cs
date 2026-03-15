namespace Shared.Contracts.Models;

public record ActivityTimeoutConfig(
    int StartToCloseMinutes,
    int? ScheduleToCloseMinutes,
    int? HeartbeatSeconds,
    RetryPolicyConfig? RetryPolicy);
