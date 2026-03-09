using System.Text.Json;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using StackExchange.Redis;

namespace NetworkA.Activities.JobSetup.Cache;

public class RedisProxyConfigCache : IProxyConfigCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IProxyConfigRepository _repository;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private const string AllConfigsKey = "v1:proxyconfig:all";

    public RedisProxyConfigCache(IConnectionMultiplexer redis, IProxyConfigRepository repository)
    {
        _redis = redis;
        _repository = repository;
    }

    public async Task<ProxyConfiguration?> GetAsync(string sourceFormat, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = $"v1:proxyconfig:{sourceFormat}";
        var cached = await db.StringGetAsync(key);

        if (cached.HasValue)
            return JsonSerializer.Deserialize<ProxyConfiguration>(cached.ToString());

        var config = await _repository.FindBySourceFormatAsync(sourceFormat, ct);
        if (config is not null)
            await db.StringSetAsync(key, JsonSerializer.Serialize(config), CacheTtl);

        return config;
    }

    public async Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var cached = await db.StringGetAsync(AllConfigsKey);

        if (cached.HasValue)
            return JsonSerializer.Deserialize<List<ProxyConfiguration>>(cached.ToString())!;

        var configs = await _repository.GetAllAsync(ct);
        await db.StringSetAsync(AllConfigsKey, JsonSerializer.Serialize(configs), CacheTtl);
        return configs;
    }

    public async Task InvalidateAsync(string sourceFormat, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync($"v1:proxyconfig:{sourceFormat}");
        await db.KeyDeleteAsync(AllConfigsKey);
    }
}
