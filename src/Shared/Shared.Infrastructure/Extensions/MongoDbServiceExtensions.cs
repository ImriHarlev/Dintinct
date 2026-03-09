using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Shared.Infrastructure.Options;

namespace Shared.Infrastructure.Extensions;

public static class MongoDbServiceExtensions
{
    public static IServiceCollection AddMongoDb(this IServiceCollection services)
    {
        services.AddSingleton<IMongoClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MongoDbOptions>>().Value;
            return new MongoClient(MongoClientSettings.FromConnectionString(opts.ConnectionString));
        });

        services.AddSingleton<IMongoDatabase>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MongoDbOptions>>().Value;
            return sp.GetRequiredService<IMongoClient>().GetDatabase(opts.DatabaseName);
        });

        return services;
    }
}
