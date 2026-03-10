using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;
using Temporalio.Extensions.Hosting;

namespace Shared.Infrastructure.Extensions;

public static class TemporalServiceExtensions
{
    public static IServiceCollection AddTemporalClient(this IServiceCollection services)
    {
        services.AddTemporalClient(opts =>
        {
            using var sp = services.BuildServiceProvider();
            var temporalOpts = sp.GetRequiredService<IOptions<TemporalOptions>>().Value;
            opts.TargetHost = temporalOpts.TargetHost;
            opts.Namespace = temporalOpts.Namespace;
        });

        return services;
    }
}
