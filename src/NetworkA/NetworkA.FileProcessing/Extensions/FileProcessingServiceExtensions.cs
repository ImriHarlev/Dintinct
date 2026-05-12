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
        services.AddScoped<IFileSplitter, ImageFileSplitter>();
        services.AddScoped<IFileSplitter, MediaFileSplitter>();
        services.AddScoped<FileSplitterFactory>();
        return services;
    }

    public static IServiceCollection AddFileConverters(this IServiceCollection services)
    {
        services.AddScoped<DefaultFileConverter>();
        services.AddScoped<IFileConverter, ImageToPngConverter>();
        services.AddScoped<IFileConverter, MediaToMp4Converter>();
        services.AddScoped<FileConverterFactory>();
        return services;
    }
}
