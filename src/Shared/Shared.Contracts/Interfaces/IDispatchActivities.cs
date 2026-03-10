namespace Shared.Contracts.Interfaces;

public interface IDispatchActivities
{
    Task RetryChunkAsync(string jobId, string chunkName);
    Task WriteHardFailAsync(string jobId, string chunkName);
}
