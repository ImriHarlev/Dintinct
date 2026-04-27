using Microsoft.Extensions.DependencyInjection;
using NetworkB.FileAssembly.Assemblers;
using NetworkB.FileAssembly.Converters;

namespace NetworkB.FileAssembly.Extensions;

public static class FileAssemblyServiceExtensions
{
    public static IServiceCollection AddFileAssemblers(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileAssembler>();
        services.AddScoped<IFileAssembler, DocsAssembler>();
        services.AddScoped<FileAssemblerFactory>();
        return services;
    }

    public static IServiceCollection AddFileConverters(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileConverter>();
        services.AddScoped<FileConverterFactory>();
        return services;
    }
}
