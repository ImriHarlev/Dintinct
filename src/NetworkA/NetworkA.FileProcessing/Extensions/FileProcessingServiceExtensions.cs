using Microsoft.Extensions.DependencyInjection;
using NetworkA.FileProcessing.Converters;
using NetworkA.FileProcessing.Splitters;

namespace NetworkA.FileProcessing.Extensions;

public static class FileProcessingServiceExtensions
{
    public static IServiceCollection AddFileSplitters(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileSplitter>();
        services.AddScoped<IFileSplitter, DocxFileSplitter>();
        services.AddScoped<FileSplitterFactory>();
        return services;
    }

    public static IServiceCollection AddFileConverters(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileConverter>();
        services.AddScoped<FileConverterFactory>();
        return services;
    }
}
