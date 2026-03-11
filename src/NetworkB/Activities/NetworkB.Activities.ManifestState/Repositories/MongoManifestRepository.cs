using MongoDB.Driver;
using NetworkB.Activities.ManifestState.Interfaces;
using Shared.Contracts.Models;

namespace NetworkB.Activities.ManifestState.Repositories;

public class MongoManifestRepository : IAssemblyBlueprintRepository
{
    private readonly IMongoCollection<AssemblyBlueprint> _collection;

    public MongoManifestRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<AssemblyBlueprint>("assembly_blueprints");
    }

    public async Task<AssemblyBlueprint?> FindByJobIdAsync(string jobId, CancellationToken ct = default)
    {
        var filter = Builders<AssemblyBlueprint>.Filter.Eq(b => b.Id, jobId);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task UpsertAsync(AssemblyBlueprint blueprint, CancellationToken ct = default)
    {
        var filter = Builders<AssemblyBlueprint>.Filter.Eq(b => b.Id, blueprint.Id);
        var options = new ReplaceOptions { IsUpsert = true };
        await _collection.ReplaceOneAsync(filter, blueprint, options, ct);
    }
}
