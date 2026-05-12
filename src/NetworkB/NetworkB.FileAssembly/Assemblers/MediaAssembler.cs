using FFMpegCore;

namespace NetworkB.FileAssembly.Assemblers;

/// <summary>
/// Assembles MP4 segments produced by MediaFileSplitter on Network A.
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
public sealed class MediaAssembler : IFileAssembler
{
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
            // Write each MP4 segment to temp files in order.
            var segmentPaths = new List<string>(request.Chunks.Count);
            for (var i = 0; i < request.Chunks.Count; i++)
            {
                var segPath = Path.Combine(tempDir, $"segment_{i}.mp4");
                await File.WriteAllBytesAsync(segPath, request.Chunks[i].Content);
                segmentPaths.Add(segPath);
            }

            // Build the FFmpeg concat list file.
            var concatListPath = Path.Combine(tempDir, "concat.txt");
            var concatLines = segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(concatListPath, concatLines);

            await FFMpegArguments
                .FromFileInput(concatListPath, false, options => options
                    .ForceFormat("concat")
                    .WithCustomArgument("-safe 0"))
                .OutputToFile(request.OutputPath, overwrite: true, options => options
                    .CopyChannel())
                .ProcessAsynchronously();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
