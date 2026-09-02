using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace NetworkB.FileAssembly.Converters;

/// <summary>
/// Converts a PNG (produced by NetworkA's <c>PdfToPngConverter</c>) back to a PDF file
/// by slicing the tall composite PNG into individual pages and embedding each slice
/// as a full-page image using PdfSharp.
/// </summary>
/// <remarks>
/// NetworkA stores all pages of a multi-page source document as a single tall PNG image
/// and records the original page count in the image's <c>PageCount</c> metadata attribute.
/// Pixel dimensions are converted to PDF points (1 pt = 1/72 inch) using the image's DPI
/// metadata, falling back to 96 DPI when none is present.
/// </remarks>
public sealed class PngToPdfConverter : IFileConverter
{
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<PngToPdfConverter> _logger;

    public PngToPdfConverter(RecyclableMemoryStreamManager streamManager, ILogger<PngToPdfConverter> logger)
    {
        _streamManager = streamManager;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        toExtension.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        _logger.LogInformation("Converting {File} (PNG) back to PDF using PdfSharp", request.SourceFilePath);

        MagickImage image;
        try
        {
            image = new MagickImage(request.SourceFilePath);
        }
        catch (MagickCorruptImageErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: corrupt or unreadable PNG '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (MagickResourceLimitErrorException ex)
        {
            _logger.LogError(ex, "ImageMagick: resource limit exceeded (image too large?) reading PNG '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (MagickException ex)
        {
            _logger.LogError(ex, "ImageMagick error reading PNG '{SourceFile}'", request.SourceFilePath);
            throw;
        }

        using (image)
        {
            // PNG attributes written by Magick.NET are read back with the "PNG:" prefix
            var pageCountStr = image.GetAttribute("PageCount")
                            ?? image.GetAttribute("PNG:PageCount");
            var pageCount = int.TryParse(pageCountStr, out var pc) && pc > 0 ? pc : 1;

            _logger.LogDebug(
                "PNG '{File}' contains {PageCount} page(s) (attribute: '{Attr}')",
                request.SourceFilePath, pageCount, pageCountStr ?? "<not set>");

            var density      = image.Density?.X > 0 ? image.Density!.X : 96.0;
            var totalWidthPx = (int)image.Width;
            var pageHeightPx = (int)image.Height / pageCount;

            var widthPt      = totalWidthPx  * 72.0 / density;
            var pageHeightPt = pageHeightPx  * 72.0 / density;

            using var pdfDocument = new PdfDocument();

            for (var i = 0; i < pageCount; i++)
            {
                // CloneArea eliminates a full-image pixel copy per page; for an N-page document this is the difference between O(N²) and O(N) memory allocations.
                using var slice = image.CloneArea(new MagickGeometry(0, i * pageHeightPx, (uint)totalWidthPx, (uint)pageHeightPx));
                slice.ResetPage();

                // Replaces new MemoryStream() for both the per-page slice stream and the output stream — pooled buffers, no GC pressure from large short-lived arrays.
                using var sliceStream = _streamManager.GetStream();
                slice.Write((Stream)sliceStream, MagickFormat.Png);
                sliceStream.Position = 0;

                var page = pdfDocument.AddPage();
                page.Width  = XUnit.FromPoint(widthPt);
                page.Height = XUnit.FromPoint(pageHeightPt);

                using var gfx    = XGraphics.FromPdfPage(page);
                using var xImage = XImage.FromStream(sliceStream);
                gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
            }

            using var outputStream = _streamManager.GetStream();
            pdfDocument.Save(outputStream, false);

            // Replaces ToArray() — no second copy of the final PDF.
            return Task.FromResult(outputStream.GetBuffer()[..(int)outputStream.Length]);
        }
    }
}
