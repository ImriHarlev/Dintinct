using ImageMagick;
using Microsoft.Extensions.Logging;

namespace NetworkB.FileAssembly.Assemblers;

/// <summary>
/// Assembles PNG strips produced by PngFileSplitter on NetworkA.
///
/// Single chunk: written directly — no reconstruction needed.
/// Multiple chunks: each chunk is a horizontal PNG strip; they are stacked
/// vertically in index order using MagickImageCollection.AppendVertically()
/// to reconstruct the full image.
/// </summary>
public sealed class PngAssembler : IFileAssembler
{
    private readonly ILogger<PngAssembler> _logger;

    public PngAssembler(ILogger<PngAssembler> logger)
    {
        _logger = logger;
    }

    public bool CanAssemble(string fileExtension) =>
        fileExtension.Equals("png", StringComparison.OrdinalIgnoreCase);

    public async Task AssembleAsync(AssemblyRequest request)
    {
        if (request.Chunks.Count == 1)
        {
            await File.WriteAllBytesAsync(request.OutputPath, request.Chunks[0].Content);
            return;
        }

        try
        {
            using var collection = new MagickImageCollection();
            foreach (var chunk in request.Chunks)
                collection.Add(new MagickImage(chunk.Content));

            using var assembled = collection.AppendVertically();
            assembled.Format = MagickFormat.Png;
            await assembled.WriteAsync(request.OutputPath);
        }
        catch (MagickCorruptImageErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: corrupt chunk data assembling PNG to '{OutputPath}'",
                request.OutputPath);
            throw;
        }
        catch (MagickResourceLimitErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: resource limit exceeded assembling {ChunkCount} PNG strips into '{OutputPath}'",
                request.Chunks.Count, request.OutputPath);
            throw;
        }
        catch (MagickException ex)
        {
            _logger.LogError(ex, "ImageMagick error assembling PNG strips into '{OutputPath}'",
                request.OutputPath);
            throw;
        }
    }
}
