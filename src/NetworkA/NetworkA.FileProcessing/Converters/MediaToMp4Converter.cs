using FFMpegCore;
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
    private readonly MediaFormatsOptions _options;

    public MediaToMp4Converter(IOptions<MediaFormatsOptions> options)
    {
        _options = options.Value;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
        _options.IsSupported(fromExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), $"dintinct_{Guid.NewGuid():N}.mp4");

        try
        {
            await FFMpegArguments
                .FromFileInput(request.SourceFilePath)
                .OutputToFile(tempOutput, overwrite: true, o => o
                    .WithVideoCodec("libx264")
                    .WithAudioCodec("aac")
                    .WithCustomArgument("-movflags +faststart"))
                .ProcessAsynchronously();

            return await File.ReadAllBytesAsync(tempOutput);
        }
        finally
        {
            if (File.Exists(tempOutput))
                File.Delete(tempOutput);
        }
    }
}
