using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;
using NetworkB.FileAssembly.Assemblers;
using NetworkB.FileAssembly.Converters;

namespace NetworkB.FileAssembly.Extensions;

public static class FileAssemblyServiceExtensions
{
    public static IServiceCollection AddFileAssemblers(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileAssembler>();
        services.AddScoped<IFileAssembler, DocsAssembler>();
        services.AddScoped<IFileAssembler, PngAssembler>();
        services.AddScoped<IFileAssembler, Mp4Assembler>();
        services.AddScoped<FileAssemblerFactory>();
        return services;
    }

    public static IServiceCollection AddFileConverters(this IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.AddScoped<DefaultFileConverter>();
        // PDF converters must be registered before the generic image/presentation
        // converters so FirstOrDefault selects them when building the factory map.
        services.AddScoped<IFileConverter, DocxToPdfConverter>();
        services.AddScoped<IFileConverter, PngToPdfConverter>();
        services.AddScoped<IFileConverter, PngToImageConverter>();
        services.AddScoped<IFileConverter, Mp4ToMediaConverter>();
        services.AddScoped<IFileConverter, DocxToPresentationConverter>();
        services.AddScoped<IFileConverter, PngToPresentationConverter>();
        services.AddScoped<FileConverterFactory>();
        return services;
    }
}
