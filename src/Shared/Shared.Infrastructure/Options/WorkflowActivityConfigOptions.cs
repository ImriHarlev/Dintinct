namespace Shared.Infrastructure.Options;

public class WorkflowActivityConfigOptions
{
    public Dictionary<string, WorkflowConfigEntry> Configs { get; set; } = new();

    public class WorkflowConfigEntry
    {
        public Dictionary<string, ActivityTimeoutEntry> Activities { get; set; } = new();
    }

    public class ActivityTimeoutEntry
    {
        public int StartToCloseMinutes { get; set; }
        public int? ScheduleToCloseMinutes { get; set; }
        public int? HeartbeatSeconds { get; set; }
        public RetryPolicyEntry? RetryPolicy { get; set; }
    }

    public class RetryPolicyEntry
    {
        public int InitialIntervalSeconds { get; set; }
        public double BackoffCoefficient { get; set; } = 2.0;
        public int MaximumIntervalSeconds { get; set; }
        public int MaximumAttempts { get; set; }
        public List<string> NonRetryableErrorTypes { get; set; } = [];
    }
}
