using FFMpegCore;
using FFMpegCore.Arguments;

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
/// Requires FFmpeg binaries to be available on the system PATH, or configured via
/// GlobalFFOptions before this splitter is invoked.
/// </summary>
public sealed class MediaFileSplitter : IFileSplitter
{
    public bool CanSplit(string fileExtension) =>
        fileExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        var fileBytes = await File.ReadAllBytesAsync(request.SourceFilePath);

        if (!request.FileSizeLimitMb.HasValue)
            return [fileBytes];

        var maxBytes = request.FileSizeLimitMb.Value * 1024 * 1024;

        if (fileBytes.Length <= maxBytes)
            return [fileBytes];

        return await SplitIntoSegmentsAsync(request.SourceFilePath, maxBytes);
    }

    private static async Task<IReadOnlyList<byte[]>> SplitIntoSegmentsAsync(
        string sourcePath,
        int maxBytes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dintinct_media_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // FFmpeg segment muxer splits at keyframe boundaries so actual sizes may
            // slightly exceed maxBytes. -reset_timestamps 1 ensures each segment has
            // timestamps starting from 0, which is required for the concat demuxer.
            var segmentPattern = Path.Combine(tempDir, "segment_%d.mp4");

            await FFMpegArguments
                .FromFileInput(sourcePath)
                .OutputToFile(segmentPattern, overwrite: true, options => options
                    .CopyChannel()
                    .ForceFormat("segment")
                    .WithCustomArgument($"-segment_size {maxBytes}")
                    .WithCustomArgument("-reset_timestamps 1"))
                .ProcessAsynchronously();

            var segmentFiles = Directory.GetFiles(tempDir, "segment_*.mp4")
                .OrderBy(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return int.TryParse(name.Replace("segment_", ""), out var idx) ? idx : 0;
                })
                .ToList();

            var chunks = new List<byte[]>(segmentFiles.Count);
            foreach (var segFile in segmentFiles)
                chunks.Add(await File.ReadAllBytesAsync(segFile));

            return chunks;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
