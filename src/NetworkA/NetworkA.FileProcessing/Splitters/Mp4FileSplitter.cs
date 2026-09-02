using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;

namespace NetworkA.FileProcessing.Splitters;

/// <summary>
/// Splits an MP4 file (produced by MediaToMp4Converter) into segments using the
/// FFmpeg segment muxer. Each segment is a self-contained, valid MP4 file so the
/// assembler on Network B can concatenate them with the concat demuxer.
///
/// If no size limit is configured, or the file already fits within the limit, a
/// single chunk is returned without invoking FFmpeg.
///
/// CanSplit is hardcoded to "mp4" because this splitter operates on the converted
/// output format, not on the original source formats.
/// 
/// The CopyChannel() call maps to FFmpeg's -c copy flag, which copies video and audio streams verbatim without re-encoding. No re-encoding = zero quality loss. 
/// The segments are byte-identical slices of the original encoded data.
///
/// The trade-off: keyframe boundaries
/// ----------------------------------
/// FFmpeg's segment muxer can only cut at keyframe (I-frame) boundaries, because a video chunk must start with a complete frame — no re-encoding means you can't insert a keyframe 
/// wherever you want.So the actual segment sizes will slightly exceed maxBytes in practice — segments round up to the next keyframe.
///
/// Downstream implication
/// ----------------------
/// Because each segment starts on a keyframe with reset timestamps(-reset_timestamps 1), the concat demuxer on Network B can stitch them cleanly by just appending, 
/// without any gap or decode artifact.
/// If you need stricter size control, the only way is to re-encode with a target bitrate (-b:v), but that loses quality.The current approach is the right call for 
/// a lossless split.
/// One minor thing to check: DefaultFileSplitter is used as a fallback — make sure it's not being hit for MP4 files by confirming FileSplitterFactory routes .mp4 to 
/// Mp4FileSplitter after conversion.
/// 
/// To summarize:
/// -------------
///  - Splits at keyframe boundaries using the FFmpeg segment muxer
///  - Uses CopyChannel() (-c copy) so no re - encoding, no quality loss
///  - Resets timestamps per segment so reassembly works cleanly
///
/// Requires FFmpeg binaries to be available on the system PATH, or configured via
/// GlobalFFOptions before this splitter is invoked.
/// </summary>
public sealed class Mp4FileSplitter : IFileSplitter
{
    private readonly ILogger<Mp4FileSplitter> _logger;

    public Mp4FileSplitter(ILogger<Mp4FileSplitter> logger)
    {
        _logger = logger;
    }

    public bool CanSplit(string fileExtension) =>
        fileExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        if (!request.FileSizeLimitMb.HasValue)
            return [await File.ReadAllBytesAsync(request.SourceFilePath)];

        var maxBytes = (long)request.FileSizeLimitMb.Value * 1024 * 1024;
        var fileSize = new FileInfo(request.SourceFilePath).Length;

        if (fileSize <= maxBytes)
            return [await File.ReadAllBytesAsync(request.SourceFilePath)];

        return await SplitIntoSegmentsAsync(request.SourceFilePath, maxBytes);
    }

    private async Task<IReadOnlyList<byte[]>> SplitIntoSegmentsAsync(
        string sourcePath,
        long maxBytes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dintinct_media_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // FFmpeg segment muxer splits at keyframe boundaries so actual sizes may
            // slightly exceed maxBytes. -reset_timestamps 1 ensures each segment has
            // timestamps starting from 0, which is required for the concat demuxer.
            var segmentPattern = Path.Combine(tempDir, "segment_%d.mp4");

            await FfmpegGate.Semaphore.WaitAsync();
            try
            {
                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(segmentPattern, overwrite: true, options => options
                        .CopyChannel()
                        .ForceFormat("segment")
                        .WithCustomArgument($"-segment_size {maxBytes}")
                        .WithCustomArgument("-reset_timestamps 1"))
                    .ProcessAsynchronously();
            }
            catch (FFMpegException ex)
            {
                _logger.LogError(ex, "FFmpeg failed splitting '{SourceFile}' into MP4 segments. FFmpeg output: {FFmpegOutput}",
                    sourcePath, ex.Message);
                throw;
            }
            finally
            {
                FfmpegGate.Semaphore.Release();
            }

            var segmentFiles = Directory.GetFiles(tempDir, "segment_*.mp4")
                .Select(f => (path: f, idx: ParseSegmentIndex(f)))
                .Where(t => t.idx >= 0)
                .OrderBy(t => t.idx)
                .Select(t => t.path)
                .ToArray();

            return await Task.WhenAll(segmentFiles.Select(f => File.ReadAllBytesAsync(f)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int ParseSegmentIndex(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return int.TryParse(stem.AsSpan("segment_".Length), out var idx) ? idx : -1;
    }
}
