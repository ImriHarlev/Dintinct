using ImageMagick;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class ImageToPngConverter : IFileConverter
{
    private readonly ImageFormatsOptions _options;

    public ImageToPngConverter(IOptions<ImageFormatsOptions> options)
    {
        _options = options.Value;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        _options.IsSupported(fromExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        using var image = new MagickImage(request.SourceFilePath);
        image.Format = MagickFormat.Png;
        using var stream = new MemoryStream();
        await image.WriteAsync(stream);
        return stream.ToArray();
    }
}
