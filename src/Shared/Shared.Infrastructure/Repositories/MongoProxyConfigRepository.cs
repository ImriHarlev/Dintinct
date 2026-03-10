using MongoDB.Driver;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;

namespace Shared.Infrastructure.Repositories;

public class MongoProxyConfigRepository : IProxyConfigRepository
{
    private readonly IMongoCollection<ProxyConfiguration> _configs;

    public MongoProxyConfigRepository(IMongoDatabase database)
    {
        _configs = database.GetCollection<ProxyConfiguration>("proxy_configurations");
    }

    public async Task<ProxyConfiguration?> FindBySourceFormatAsync(string sourceFormat, CancellationToken ct = default)
    {
        var filter = Builders<ProxyConfiguration>.Filter.Eq(c => c.SourceFormat, sourceFormat);
        return await _configs.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        return await _configs.Find(Builders<ProxyConfiguration>.Filter.Empty).ToListAsync(ct);
    }
}
