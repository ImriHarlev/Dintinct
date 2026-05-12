using ImageMagick;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Splitters;

/// <summary>
/// Splits an image into horizontal strips whose PNG-encoded size each fit within
/// FileSizeLimitMb. Uses the same binary-search approach as DocxFileSplitter to
/// find the maximum strip height without exceeding the size cap.
///
/// If no size limit is configured, or the whole image already fits, a single
/// chunk is returned. Only the first frame of multi-frame images (e.g. animated
/// GIFs, multi-page TIFFs) is used; full multi-frame support is a future concern.
///
/// This splitter is selected by the *converted* extension (png) so it runs after
/// ImageToPngConverter has already produced the PNG bytes written to the temp file.
/// </summary>
public sealed class ImageFileSplitter : IFileSplitter
{
    private readonly ImageFormatsOptions _options;

    public ImageFileSplitter(IOptions<ImageFormatsOptions> options)
    {
        _options = options.Value;
    }

    public bool CanSplit(string fileExtension) =>
        _options.IsSupported(fileExtension);

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        var fileBytes = await File.ReadAllBytesAsync(request.SourceFilePath);

        if (!request.FileSizeLimitMb.HasValue)
            return [fileBytes];

        var maxBytes = request.FileSizeLimitMb.Value * 1024 * 1024;

        if (fileBytes.Length <= maxBytes)
            return [fileBytes];

        // Load only the first frame — multi-frame support (animated GIF, multi-page
        // TIFF) is handled at the conversion stage and is a known POC limitation here.
        using var image = new MagickImage(fileBytes);
        return SplitIntoStrips(image, maxBytes);
    }

    private static IReadOnlyList<byte[]> SplitIntoStrips(MagickImage image, int maxBytes)
    {
        var totalHeight = (int)image.Height;
        var stripHeight = FindMaxStripHeight(image, maxBytes);

        var strips = new List<byte[]>();
        for (var y = 0; y < totalHeight; y += stripHeight)
        {
            var currentHeight = Math.Min(stripHeight, totalHeight - y);
            strips.Add(EncodeStrip(image, y, currentHeight));
        }

        return strips;
    }

    // Binary search for the largest strip height whose PNG-encoded size ≤ maxBytes.
    private static int FindMaxStripHeight(MagickImage image, int maxBytes)
    {
        var totalHeight = (int)image.Height;
        var low = 1;
        var high = totalHeight;
        var best = 1;

        while (low <= high)
        {
            var candidate = low + ((high - low) / 2);
            var encoded = EncodeStrip(image, 0, candidate);

            if (encoded.Length <= maxBytes)
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        return best;
    }

    private static byte[] EncodeStrip(MagickImage image, int yOffset, int height)
    {
        using var strip = image.Clone();
        strip.Crop(new MagickGeometry(0, yOffset, image.Width, (uint)height));
        strip.Format = MagickFormat.Png;
        using var stream = new MemoryStream();
        strip.Write(stream);
        return stream.ToArray();
    }
}
