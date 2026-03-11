using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetworkA.Activities.JobSetup.Options;
using StackExchange.Redis;

namespace NetworkA.Activities.JobSetup.Extensions;

public static class RedisServiceExtensions
{
    public static IServiceCollection AddRedis(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            var config = ConfigurationOptions.Parse(opts.Endpoint);
            config.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(config);
        });

        return services;
    }
}
