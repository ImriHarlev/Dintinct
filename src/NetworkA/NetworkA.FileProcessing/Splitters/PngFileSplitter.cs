using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.IO;

namespace NetworkA.FileProcessing.Splitters;

/// <summary>
/// Splits a PNG image into horizontal strips whose PNG-encoded size each fit within
/// FileSizeLimitMb.
///
/// If no size limit is configured, or the whole image already fits, a single chunk
/// is returned. Only the first frame of multi-frame images (e.g. animated GIFs,
/// multi-page TIFFs) is used; full multi-frame support is a known POC limitation.
///
/// This splitter is selected by the converted extension "png" — it always receives
/// PNG bytes produced by ImageToPngConverter or PresentationToPngConverter.
/// 
/// Why this preserves quality:
///  - Decoding any image to raw pixels and re-encoding as PNG is lossless — PNG stores exact pixel values.
///  - The strip encoding(strip.Format = MagickFormat.Png) is also PNG, so no lossy re - compression ever happens.
///
/// Why strip heights are estimated rather than searched:
///  - Encoding a strip of a large PNG is expensive, and on incompressible content deflate
///    dominates the runtime. A binary search over strip height costs O(log height) encodes
///    per probe position before a single chunk is emitted — tens of full-size encodes.
///  - PNG size is very nearly linear in row count for a given image, so the source's own
///    bytes-per-row predicts a fitting height directly. Each emitted strip is then encoded
///    once and rescaled only if it actually overshoots, so the common case is one encode
///    per chunk.
/// </summary>
public sealed class PngFileSplitter : IFileSplitter
{
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<PngFileSplitter> _logger;

    public PngFileSplitter(RecyclableMemoryStreamManager streamManager, ILogger<PngFileSplitter> logger)
    {
        _streamManager = streamManager;
        _logger = logger;
    }

    public bool CanSplit(string fileExtension) =>
        fileExtension.Equals("png", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        byte[] fileBytes;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(request.SourceFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read PNG file '{SourceFile}'", request.SourceFilePath);
            throw;
        }

        if (!request.FileSizeLimitMb.HasValue)
            return [fileBytes];

        // Cast to long before multiplying — int * int overflows for limits >= 2048 MB.
        var maxBytes = (long)request.FileSizeLimitMb.Value * 1024 * 1024;

        if (fileBytes.Length <= maxBytes)
            return [fileBytes];

        _logger.LogInformation(
            "Splitting PNG '{SourceFile}' ({Bytes:N0} bytes, limit {LimitMb} MB)",
            request.SourceFilePath, fileBytes.Length, request.FileSizeLimitMb.Value);

        try
        {
            using var image = new MagickImage(fileBytes);
            var strips = SplitIntoStrips(image, maxBytes, fileBytes.Length);
            _logger.LogInformation(
                "Split '{SourceFile}' into {Count} strip(s)",
                request.SourceFilePath, strips.Count);
            return strips;
        }
        catch (MagickCorruptImageErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: corrupt or unreadable PNG data from '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
        catch (MagickResourceLimitErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: resource limit exceeded splitting PNG '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
        catch (MagickException ex)
        {
            _logger.LogError(ex, "ImageMagick error splitting PNG '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
    }

    // Walks the image top to bottom, emitting each strip as soon as it is known to fit.
    // Sequential: MagickImage.CloneArea reads the source pixel cache and is not documented
    // as safe for concurrent access from multiple threads.
    private IReadOnlyList<byte[]> SplitIntoStrips(MagickImage image, long maxBytes, long encodedLength)
    {
        var totalHeight = (int)image.Height;
        var stripHeight = EstimateStripHeight(totalHeight, maxBytes, encodedLength);
        var strips = new List<byte[]>();
        var y = 0;

        while (y < totalHeight)
        {
            var height = Math.Min(stripHeight, totalHeight - y);
            var encoded = EncodeStrip(image, y, height);

            // Content density varies down the image, so a height that fits elsewhere can still
            // overshoot here. Rescaling by the observed ratio converges in one step for all but
            // pathological content, and the strictly decreasing height guarantees termination.
            while (encoded.Length > maxBytes && height > 1)
            {
                var rescaled = (int)(height * (maxBytes / (double)encoded.Length) * 0.95);
                height = Math.Clamp(rescaled, 1, height - 1);

                _logger.LogDebug(
                    "Strip at row {Row} exceeded {MaxBytes:N0} bytes, retrying at height {Height}",
                    y, maxBytes, height);

                encoded = EncodeStrip(image, y, height);

                // Carry the correction forward so later strips don't repeat the same overshoot.
                stripHeight = height;
            }

            if (encoded.Length > maxBytes)
                _logger.LogWarning(
                    "Single image row at {Row} encodes to {Bytes:N0} bytes, above the {MaxBytes:N0} byte limit — emitting oversized strip",
                    y, encoded.Length, maxBytes);

            strips.Add(encoded);
            y += height;
        }

        return strips;
    }

    // PNG size is near-linear in row count, so the source file's own bytes-per-row converts
    // the byte budget straight into a row budget. The 0.98 margin absorbs the per-strip
    // overhead a whole-file average cannot see: signature, IHDR/IEND, and a deflate stream
    // that restarts with a cold window on every strip.
    private static int EstimateStripHeight(int totalHeight, long maxBytes, long encodedLength)
    {
        var bytesPerRow = Math.Max(1.0, (double)encodedLength / totalHeight);
        return Math.Clamp((int)(maxBytes / bytesPerRow * 0.98), 1, totalHeight);
    }

    private byte[] EncodeStrip(MagickImage image, int yOffset, int height)
    {
        using var strip = image.CloneArea(new MagickGeometry(0, yOffset, image.Width, (uint)height));
        strip.Format = MagickFormat.Png;
        using var stream = _streamManager.GetStream();
        strip.Write((Stream)stream);
        return stream.GetBuffer()[..(int)stream.Length];
    }
}
