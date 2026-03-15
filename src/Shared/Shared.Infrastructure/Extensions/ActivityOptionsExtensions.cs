using Shared.Contracts.Models;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Shared.Infrastructure.Extensions;

public static class ActivityOptionsExtensions
{
    public static ActivityOptions ToActivityOptions(
        this WorkflowActivityConfig config,
        string activityName,
        string taskQueue)
    {
        var cfg = config.Activities[activityName];
        return new ActivityOptions
        {
            TaskQueue = taskQueue,
            StartToCloseTimeout = TimeSpan.FromMinutes(cfg.StartToCloseMinutes),
            ScheduleToCloseTimeout = cfg.ScheduleToCloseMinutes.HasValue
                ? TimeSpan.FromMinutes(cfg.ScheduleToCloseMinutes.Value) : null,
            HeartbeatTimeout = cfg.HeartbeatSeconds.HasValue
                ? TimeSpan.FromSeconds(cfg.HeartbeatSeconds.Value) : null,
            RetryPolicy = cfg.RetryPolicy is { } rp ? new RetryPolicy
            {
                InitialInterval = TimeSpan.FromSeconds(rp.InitialIntervalSeconds),
                BackoffCoefficient = (float)rp.BackoffCoefficient,
                MaximumInterval = TimeSpan.FromSeconds(rp.MaximumIntervalSeconds),
                MaximumAttempts = rp.MaximumAttempts,
                NonRetryableErrorTypes = rp.NonRetryableErrorTypes
            } : null
        };
    }
}
