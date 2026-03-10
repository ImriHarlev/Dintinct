using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Shared.Infrastructure.Startup;

/// <summary>
/// Verifies infrastructure connectivity at startup.
/// Log format: [INF] {ServiceName}: {Dependency} connected successfully
/// On failure: [ERR] {ServiceName}: Failed to connect to {Dependency}: {message} — then exits non-zero.
/// </summary>
public static class StartupValidator
{
    public static async Task ValidateMongoDbAsync(IMongoDatabase database, string serviceName, ILogger logger)
    {
        try
        {
            await database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
                new MongoDB.Driver.JsonCommand<MongoDB.Bson.BsonDocument>("{ping:1}"));
            logger.LogInformation("{ServiceName}: MongoDB connected successfully", serviceName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ServiceName}: Failed to connect to MongoDB: {Message}", serviceName, ex.Message);
            Environment.Exit(1);
        }
    }

    public static async Task ValidateRedisAsync(IConnectionMultiplexer redis, string serviceName, ILogger logger)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.PingAsync();
            logger.LogInformation("{ServiceName}: Redis connected successfully", serviceName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ServiceName}: Failed to connect to Redis: {Message}", serviceName, ex.Message);
            Environment.Exit(1);
        }
    }

    public static void LogTemporalWorkerRegistered(string serviceName, string taskQueue, ILogger logger)
    {
        logger.LogInformation("{ServiceName}: Temporal worker registered on queue {TaskQueue}", serviceName, taskQueue);
    }
}
