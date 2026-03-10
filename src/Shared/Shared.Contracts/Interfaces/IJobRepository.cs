using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IJobRepository
{
    Task<Job?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<Job?> FindByExternalIdAsync(string externalId, CancellationToken ct = default);
    Task UpsertAsync(Job job, CancellationToken ct = default);
    Task IncrementChunkRetryCountAsync(string jobId, string chunkName, CancellationToken ct = default);
}
