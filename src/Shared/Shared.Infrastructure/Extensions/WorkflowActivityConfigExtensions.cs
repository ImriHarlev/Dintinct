using Microsoft.Extensions.DependencyInjection;
using Shared.Infrastructure.Cache;
using Shared.Infrastructure.Repositories;

namespace Shared.Infrastructure.Extensions;

public static class WorkflowActivityConfigExtensions
{
    public static IServiceCollection AddWorkflowActivityConfig(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowActivityConfigRepository, MongoWorkflowActivityConfigRepository>();
        services.AddScoped<IWorkflowActivityConfigCache, RedisWorkflowActivityConfigCache>();
        return services;
    }
}
