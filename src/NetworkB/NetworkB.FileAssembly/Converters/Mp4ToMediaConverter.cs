using FFMpegCore;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

public sealed class Mp4ToMediaConverter : IFileConverter
{
    private readonly ILogger<Mp4ToMediaConverter> _logger;
    private readonly ProxyConfigOptions _proxy;

    public Mp4ToMediaConverter(IOptions<ProxyConfigOptions> proxy, ILogger<Mp4ToMediaConverter> logger)
    {
        _proxy = proxy.Value;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
        !toExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
        _proxy.HasConversion(toExtension, "mp4");

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        var tempOutput = Path.ChangeExtension(
            Path.Combine(Path.GetTempPath(), $"dintinct_{Guid.NewGuid():N}"),
            request.ToExtension);

        try
        {
            await FFMpegArguments
                .FromFileInput(request.SourceFilePath)
                .OutputToFile(tempOutput, overwrite: true)
                .ProcessAsynchronously();

            return await File.ReadAllBytesAsync(tempOutput);
        }
        catch (FFMpegException ex)
        {
            _logger.LogError(ex, "FFmpeg failed converting '{SourceFile}' (mp4) to '{ToExt}'. FFmpeg output: {FFmpegOutput}",
                request.SourceFilePath, request.ToExtension, ex.Message);
            throw;
        }
        finally
        {
            if (File.Exists(tempOutput))
                File.Delete(tempOutput);
        }
    }
}
