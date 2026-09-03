using FFMpegCore;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;

namespace NetworkB.FileAssembly.Assemblers;

/// <summary>
/// Assembles MP4 segments produced by Mp4FileSplitter on Network A.
///
/// Single chunk: written directly — no reconstruction needed.
/// Multiple chunks: each chunk is a valid MP4 segment; they are concatenated in
/// index order using the FFmpeg concat demuxer (-c copy) to reconstruct the full file.
///
/// CanAssemble is hardcoded to "mp4" because this assembler operates on the converted
/// output format.
///
/// Requires FFmpeg binaries to be available on the system PATH, or configured via
/// GlobalFFOptions before this assembler is invoked.
/// </summary>
public sealed class Mp4Assembler : IFileAssembler
{
    private readonly ILogger<Mp4Assembler> _logger;

    public Mp4Assembler(ILogger<Mp4Assembler> logger)
    {
        _logger = logger;
    }

    public bool CanAssemble(string fileExtension) =>
        fileExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase);

    public async Task AssembleAsync(AssemblyRequest request)
    {
        if (request.Chunks.Count == 1)
        {
            await File.WriteAllBytesAsync(request.OutputPath, request.Chunks[0].Content);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dintinct_assembly_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Pre-compute all segment paths then write in parallel — each goes to a distinct file.
            var segmentPaths = new string[request.Chunks.Count];
            for (var i = 0; i < request.Chunks.Count; i++)
                segmentPaths[i] = Path.Combine(tempDir, $"segment_{i}.mp4");

            await Task.WhenAll(request.Chunks.Select((chunk, i) =>
                File.WriteAllBytesAsync(segmentPaths[i], chunk.Content)));

            // Build the FFmpeg concat list file.
            var concatListPath = Path.Combine(tempDir, "concat.txt");
            var concatLines = segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(concatListPath, concatLines);

            try
            {
                await FFMpegArguments
                    .FromFileInput(concatListPath, false, options => options
                        .ForceFormat("concat")
                        .WithCustomArgument("-safe 0"))
                    .OutputToFile(request.OutputPath, overwrite: true, options => options
                        .CopyChannel())
                    .ProcessAsynchronously();
            }
            catch (FFMpegException ex)
            {
                _logger.LogError(ex, "FFmpeg failed assembling {ChunkCount} MP4 segments into '{OutputPath}'. FFmpeg output: {FFmpegOutput}",
                    request.Chunks.Count, request.OutputPath, ex.Message);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
