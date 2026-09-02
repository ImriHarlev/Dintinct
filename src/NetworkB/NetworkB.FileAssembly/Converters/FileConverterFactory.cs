using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

public sealed class FileConverterFactory
{
    private readonly Dictionary<(string From, string To), IFileConverter> _map;
    private readonly DefaultFileConverter _defaultConverter;

    public FileConverterFactory(
        IEnumerable<IFileConverter> converters,
        DefaultFileConverter defaultConverter,
        IOptions<ProxyConfigOptions> proxy,
        ILogger<FileConverterFactory> logger)
    {
        _defaultConverter = defaultConverter;

        // In NetworkB the lookup is inverted: "from" is the intermediate format
        // (e.g. "docx") and "to" is the original source format (e.g. "pdf").
        var converterList = converters.ToList();
        _map = proxy.Value.Configurations
            .SelectMany(c => c.RequiredConversion
                .Select(target => (
                    From: target.ToLowerInvariant(),
                    To:   c.SourceFormat.ToLowerInvariant())))
            .ToDictionary(
                pair => (pair.From, pair.To),
                pair => converterList.FirstOrDefault(c =>
                    c.CanConvert(pair.From, pair.To)) ?? (IFileConverter)defaultConverter);

        foreach (var (key, converter) in _map)
            logger.LogDebug("Converter mapped: {From} → {To} = {Converter}", key.From, key.To, converter.GetType().Name);

        if (_map.Count == 0)
            logger.LogWarning("FileConverterFactory: no converters mapped — ProxyConfig may not have loaded.");
    }

    public IFileConverter GetConverter(string fromExtension, string toExtension) =>
        _map.TryGetValue(
            (fromExtension.ToLowerInvariant(), toExtension.ToLowerInvariant()),
            out var converter)
            ? converter
            : _defaultConverter;
}
