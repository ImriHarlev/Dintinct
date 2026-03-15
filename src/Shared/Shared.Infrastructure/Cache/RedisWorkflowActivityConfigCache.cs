using System.Text.Json;
using Shared.Contracts.Models;
using Shared.Infrastructure.Repositories;
using StackExchange.Redis;

namespace Shared.Infrastructure.Cache;

public class RedisWorkflowActivityConfigCache : IWorkflowActivityConfigCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IWorkflowActivityConfigRepository _repository;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private const string HashKey = "wfactivityconfig";

    public RedisWorkflowActivityConfigCache(
        IConnectionMultiplexer redis,
        IWorkflowActivityConfigRepository repository)
    {
        _redis = redis;
        _repository = repository;
    }

    public async Task<WorkflowActivityConfig?> GetAsync(string workflowKey, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var cached = await db.HashGetAsync(HashKey, workflowKey);

        if (cached.HasValue)
            return JsonSerializer.Deserialize<WorkflowActivityConfig>((byte[])cached!);

        var config = await _repository.GetByWorkflowKeyAsync(workflowKey, ct);
        if (config is not null)
        {
            _ = db.HashSetAsync(HashKey, workflowKey, JsonSerializer.SerializeToUtf8Bytes(config));
            _ = db.KeyExpireAsync(HashKey, CacheTtl);
        }

        return config;
    }

    public async Task InvalidateAsync(string workflowKey, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.HashDeleteAsync(HashKey, workflowKey);
    }
}
