using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class ImageToPngConverter : IFileConverter
{
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<ImageToPngConverter> _logger;
    private readonly ProxyConfigOptions _proxy;

    public ImageToPngConverter(
        IOptions<ProxyConfigOptions> proxy,
        RecyclableMemoryStreamManager streamManager,
        ILogger<ImageToPngConverter> logger)
    {
        _proxy = proxy.Value;
        _streamManager = streamManager;
        _logger = logger;
    }

    /// <summary>
    /// Handles formats whose RequiredConversion is exclusively ["PNG"] — i.e. plain image
    /// formats. Formats that produce both PNG and other outputs (e.g. pptx → DOCX + PNG)
    /// are handled by their own dedicated converters.
    /// </summary>
    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        !fromExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        _proxy.IsExclusiveConversion(fromExtension, "PNG");

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        _logger.LogInformation("Converting '{SourceFile}' ({FromExt}) to PNG",
            request.SourceFilePath, request.FromExtension);

        try
        {
            using var image = new MagickImage(request.SourceFilePath);
            image.Format = MagickFormat.Png;
            using var stream = _streamManager.GetStream();
            await image.WriteAsync(stream);
            // Slices the underlying pooled array directly instead of creating a second full copy
            var result = stream.GetBuffer()[..(int)stream.Length];
            _logger.LogInformation("Converted '{SourceFile}' to PNG ({Bytes:N0} bytes)",
                request.SourceFilePath, result.Length);
            return result;
        }
        catch (MagickCorruptImageErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: corrupt or unreadable image '{SourceFile}' ({FromExt})",
                request.SourceFilePath, request.FromExtension);
            throw;
        }
        catch (MagickResourceLimitErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: resource limit exceeded (image too large?) converting '{SourceFile}' ({FromExt})",
                request.SourceFilePath, request.FromExtension);
            throw;
        }
        catch (MagickException ex)
        {
            _logger.LogError(ex, "ImageMagick error converting '{SourceFile}' ({FromExt}) to PNG",
                request.SourceFilePath, request.FromExtension);
            throw;
        }
    }
}
