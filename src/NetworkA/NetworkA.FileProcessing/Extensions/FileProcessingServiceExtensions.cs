using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;
using NetworkA.FileProcessing.Converters;
using NetworkA.FileProcessing.Splitters;

namespace NetworkA.FileProcessing.Extensions;

public static class FileProcessingServiceExtensions
{
    public static IServiceCollection AddFileSplitters(this IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.AddScoped<DefaultFileSplitter>();
        services.AddScoped<IFileSplitter, PngFileSplitter>();
        services.AddScoped<IFileSplitter, DocxFileSplitter>();
        services.AddScoped<IFileSplitter, Mp4FileSplitter>();
        services.AddScoped<FileSplitterFactory>();
        return services;
    }

    public static IServiceCollection AddFileConverters(this IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.AddScoped<DefaultFileConverter>();
        // PDF converters must be registered before the generic image/presentation
        // converters so FirstOrDefault selects them when building the factory map.
        services.AddScoped<IFileConverter, PdfToPngConverter>();
        services.AddScoped<IFileConverter, PdfToDocxConverter>();
        services.AddScoped<IFileConverter, ImageToPngConverter>();
        services.AddScoped<IFileConverter, MediaToMp4Converter>();
        services.AddScoped<IFileConverter, PresentationToPngConverter>();
        services.AddScoped<IFileConverter, PresentationToDocxConverter>();
        services.AddScoped<FileConverterFactory>();
        return services;
    }
}
