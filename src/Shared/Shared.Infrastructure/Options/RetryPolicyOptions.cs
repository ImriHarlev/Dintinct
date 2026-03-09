namespace Shared.Infrastructure.Options;

public class RetryPolicyOptions
{
    public int MaxRetryCount { get; set; } = 3;
}
