using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;
using StackExchange.Redis;

namespace Shared.Infrastructure.Extensions;

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
