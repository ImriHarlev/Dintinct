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
    private const string HashKey = "proxyconfig";

    public RedisProxyConfigCache(IConnectionMultiplexer redis, IProxyConfigRepository repository)
    {
        _redis = redis;
        _repository = repository;
    }

    public async Task<ProxyConfiguration?> GetAsync(string sourceFormat, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var cached = await db.HashGetAsync(HashKey, sourceFormat);

        if (cached.HasValue)
            return JsonSerializer.Deserialize<ProxyConfiguration>(((byte[]?)cached)!);

        var config = await _repository.FindBySourceFormatAsync(sourceFormat, ct);
        if (config is not null)
        {
            _ = db.HashSetAsync(HashKey, sourceFormat, JsonSerializer.SerializeToUtf8Bytes(config));
            _ = db.KeyExpireAsync(HashKey, CacheTtl);
        }

        return config;
    }

    public async Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var entries = await db.HashGetAllAsync(HashKey);

        if (entries.Length > 0)
            return entries
                .Select(e => JsonSerializer.Deserialize<ProxyConfiguration>(((byte[]?)e.Value)!)!)
                .ToList();

        var configs = await _repository.GetAllAsync(ct);
        var hashFields = configs
            .Select(c => new HashEntry(c.SourceFormat, JsonSerializer.SerializeToUtf8Bytes(c)))
            .ToArray();

        _ = db.HashSetAsync(HashKey, hashFields)
            .ContinueWith(_ => db.KeyExpireAsync(HashKey, CacheTtl));

        return configs;
    }

    public async Task InvalidateAsync(string sourceFormat, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.HashDeleteAsync(HashKey, sourceFormat);
    }
}
