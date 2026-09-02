using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

public sealed class PngToImageConverter : IFileConverter
{
    private readonly ILogger<PngToImageConverter> _logger;
    private readonly ProxyConfigOptions _proxy;

    public PngToImageConverter(IOptions<ProxyConfigOptions> proxy, ILogger<PngToImageConverter> logger)
    {
        _proxy = proxy.Value;
        _logger = logger;
    }

    /// <summary>
    /// Handles back-conversion from PNG to the original image format.
    /// Excludes "png" itself (no-op passthrough). Only matches formats whose
    /// RequiredConversion is exclusively ["PNG"] so presentation formats that also
    /// produce other outputs are handled by their own dedicated converters.
    /// </summary>
    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        !toExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        _proxy.IsExclusiveConversion(toExtension, "PNG");

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        if (!Enum.TryParse<MagickFormat>(request.ToExtension, ignoreCase: true, out var format))
            throw new InvalidOperationException(
                $"Cannot resolve MagickFormat for image extension '{request.ToExtension}'.");

        try
        {
            using var image = new MagickImage(request.SourceFilePath);
            image.Format = format;
            using var stream = new MemoryStream();
            await image.WriteAsync(stream);
            return stream.ToArray();
        }
        catch (MagickCorruptImageErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: corrupt or unreadable PNG '{SourceFile}' converting to '{ToExt}'",
                request.SourceFilePath, request.ToExtension);
            throw;
        }
        catch (MagickResourceLimitErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: resource limit exceeded converting PNG '{SourceFile}' to '{ToExt}'",
                request.SourceFilePath, request.ToExtension);
            throw;
        }
        catch (MagickException ex)
        {
            _logger.LogError(ex, "ImageMagick error converting PNG '{SourceFile}' to '{ToExt}'",
                request.SourceFilePath, request.ToExtension);
            throw;
        }
    }
}
