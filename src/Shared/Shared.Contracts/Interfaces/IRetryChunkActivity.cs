namespace Shared.Contracts.Interfaces;

public interface IRetryChunkActivity
{
    Task RetryChunkAsync(string jobId, string chunkName);
}
