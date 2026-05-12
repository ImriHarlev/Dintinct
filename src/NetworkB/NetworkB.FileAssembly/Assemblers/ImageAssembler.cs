using ImageMagick;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Assemblers;

/// <summary>
/// Assembles image chunks produced by ImageFileSplitter on Network A.
///
/// Single chunk: written directly — no reconstruction needed.
/// Multiple chunks: each chunk is a horizontal PNG strip; they are stacked
/// vertically in index order using MagickImageCollection.AppendVertically()
/// to reconstruct the full image.
/// </summary>
public sealed class ImageAssembler : IFileAssembler
{
    private readonly ImageFormatsOptions _options;

    public ImageAssembler(IOptions<ImageFormatsOptions> options)
    {
        _options = options.Value;
    }

    public bool CanAssemble(string fileExtension) =>
        _options.IsSupported(fileExtension);

    public async Task AssembleAsync(AssemblyRequest request)
    {
        if (request.Chunks.Count == 1)
        {
            await File.WriteAllBytesAsync(request.OutputPath, request.Chunks[0].Content);
            return;
        }

        // Multiple chunks are horizontal strips — stack them vertically to
        // reconstruct the original image. Chunks are already sorted by index
        // by AssembleFilesActivities before this method is called.
        using var collection = new MagickImageCollection();
        foreach (var chunk in request.Chunks)
            collection.Add(new MagickImage(chunk.Content));

        using var assembled = collection.AppendVertically();
        assembled.Format = MagickFormat.Png;
        await assembled.WriteAsync(request.OutputPath);
    }
}
