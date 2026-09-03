using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

/// <summary>
/// Converts any supported video or audio format to MP4 (H.264 + AAC) using FFmpeg.
///
/// A single FFmpeg pass is used for both video and audio-only sources. When the source
/// has no video stream FFmpeg omits the video track automatically — no prior probe is needed.
///
/// Requires FFmpeg binaries to be available on the system PATH, or configured via
/// GlobalFFOptions before this converter is invoked.
/// </summary>
public sealed class MediaToMp4Converter : IFileConverter
{
    private readonly ILogger<MediaToMp4Converter> _logger;
    private readonly ProxyConfigOptions _proxy;

    public MediaToMp4Converter(IOptions<ProxyConfigOptions> proxy, ILogger<MediaToMp4Converter> logger)
    {
        _proxy = proxy.Value;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
        _proxy.HasConversion(fromExtension, "mp4");

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), $"dintinct_{Guid.NewGuid():N}.mp4");

        _logger.LogInformation("Converting '{SourceFile}' ({FromExt}) to mp4",
            request.SourceFilePath, request.FromExtension);

        try
        {
            await FfmpegGate.Semaphore.WaitAsync();
            try
            {
                await FFMpegArguments
                    .FromFileInput(request.SourceFilePath, addArguments: o => o
                        // MPEG-1/2 files often have non-monotonic DTS/PTS from B-frame reordering.
                        // +genpts regenerates presentation timestamps from DTS before decoding,
                        // preventing "invalid, non monotonically increasing dts" muxer failures.
                        .WithCustomArgument("-fflags +genpts"))
                    .OutputToFile(tempOutput, overwrite: true, o => o
                        .WithVideoCodec("libx264")
                        // "Veryfast" encodes roughly 4× faster than medium at the cost of ~10–15% larger output files — a sensible trade-off for a processing pipeline.
                        .WithSpeedPreset(Speed.VeryFast)
                        .WithAudioCodec("aac")
                        .WithCustomArgument("-movflags +faststart"))
                    .ProcessAsynchronously();
            }
            catch (FFMpegException ex)
            {
                _logger.LogError(ex, "FFmpeg failed converting '{SourceFile}' ({FromExt}) to mp4. FFmpeg output: {FFmpegOutput}",
                    request.SourceFilePath, request.FromExtension, ex.Message);
                throw;
            }
            finally
            {
                FfmpegGate.Semaphore.Release();
            }

            byte[] result;
            try
            {
                result = await File.ReadAllBytesAsync(tempOutput);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to read FFmpeg output for '{SourceFile}'",
                    request.SourceFilePath);
                throw;
            }

            _logger.LogInformation("Converted '{SourceFile}' to mp4 ({Bytes:N0} bytes)",
                request.SourceFilePath, result.Length);

            return result;
        }
        finally
        {
            if (File.Exists(tempOutput))
                File.Delete(tempOutput);
        }
    }
}
