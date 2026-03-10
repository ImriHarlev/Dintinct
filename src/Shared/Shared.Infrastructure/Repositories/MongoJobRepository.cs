using MongoDB.Driver;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;

namespace Shared.Infrastructure.Repositories;

public class MongoJobRepository : IJobRepository
{
    private readonly IMongoCollection<Job> _jobs;

    public MongoJobRepository(IMongoDatabase database)
    {
        _jobs = database.GetCollection<Job>("jobs");
    }

    public async Task<Job?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<Job>.Filter.Eq(j => j.Id, id);
        return await _jobs.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Job?> FindByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        var filter = Builders<Job>.Filter.Eq(j => j.ExternalId, externalId);
        return await _jobs.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task UpsertAsync(Job job, CancellationToken ct = default)
    {
        var filter = Builders<Job>.Filter.Eq(j => j.Id, job.Id);
        var options = new ReplaceOptions { IsUpsert = true };
        await _jobs.ReplaceOneAsync(filter, job, options, ct);
    }

    public async Task IncrementChunkRetryCountAsync(string jobId, string chunkName, CancellationToken ct = default)
    {
        // MongoDB dot-notation treats '.' as a path separator, so dots in the chunk name
        // (e.g. file extensions) must be replaced before building the update path.
        var safeKey = chunkName.Replace('.', '_');
        var filter = Builders<Job>.Filter.Eq(j => j.Id, jobId);
        var update = Builders<Job>.Update.Inc($"{nameof(Job.ChunkRetryCounters)}.{safeKey}", 1);
        await _jobs.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task UpdateStatusAsync(string jobId, string status, CancellationToken ct = default)
    {
        var filter = Builders<Job>.Filter.Eq(j => j.Id, jobId);
        var update = Builders<Job>.Update
            .Set(j => j.Status, status)
            .Set(j => j.UpdatedAt, DateTime.UtcNow);
        await _jobs.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
