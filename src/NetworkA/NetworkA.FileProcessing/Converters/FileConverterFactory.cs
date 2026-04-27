namespace NetworkA.FileProcessing.Converters;

public sealed class FileConverterFactory
{
    private readonly IEnumerable<IFileConverter> _converters;
    private readonly DefaultFileConverter _defaultConverter;

    public FileConverterFactory(IEnumerable<IFileConverter> converters, DefaultFileConverter defaultConverter)
    {
        _converters = converters;
        _defaultConverter = defaultConverter;
    }

    public IFileConverter GetConverter(string fromExtension, string toExtension)
    {
        return _converters.FirstOrDefault(c => c.CanConvert(fromExtension, toExtension)) ?? _defaultConverter;
    }
}
