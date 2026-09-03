using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class FileConverterFactory
{
    private readonly Dictionary<(string From, string To), IFileConverter> _map;
    private readonly DefaultFileConverter _defaultConverter;

    public FileConverterFactory(
        IEnumerable<IFileConverter> converters,
        DefaultFileConverter defaultConverter,
        IOptions<ProxyConfigOptions> proxy)
    {
        _defaultConverter = defaultConverter;

        var converterList = converters.ToList();
        _map = proxy.Value.Configurations
            .SelectMany(c => c.RequiredConversion
                .Select(target => (
                    From: c.SourceFormat.ToLowerInvariant(),
                    To:   target.ToLowerInvariant())))
            .ToDictionary(
                pair => (pair.From, pair.To),
                pair => converterList.FirstOrDefault(c =>
                    c.CanConvert(pair.From, pair.To)) ?? (IFileConverter)defaultConverter);
    }

    public IFileConverter GetConverter(string fromExtension, string toExtension) =>
        _map.TryGetValue(
            (fromExtension.ToLowerInvariant(), toExtension.ToLowerInvariant()),
            out var converter)
            ? converter
            : _defaultConverter;
}
