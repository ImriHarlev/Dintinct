using Aspose.Pdf;
using Aspose.Pdf.Devices;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class PdfToPngConverter : IFileConverter
{
    private static readonly Lock PdfLicenseLock = new();
    private static bool _pdfLicenseConfigured;

    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<PdfToPngConverter> _logger;

    public PdfToPngConverter(
        IOptions<AsposeOptions> asposeOptions,
        ILogger<PdfToPngConverter> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("pdf", StringComparison.OrdinalIgnoreCase) &&
        toExtension.Equals("png", StringComparison.OrdinalIgnoreCase);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsurePdfLicenseConfigured();

        _logger.LogInformation("Converting {File} to a single scan-like PNG via Aspose.PDF", request.SourceFilePath);

        Document doc;
        try
        {
            doc = new Document(request.SourceFilePath);
        }
        catch (InvalidPasswordException ex)
        {   // PDF is password-protected
            _logger.LogError(ex, "PDF is password-protected: '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (PdfException ex)
        {
            _logger.LogError(ex, "Aspose.PDF could not open '{SourceFile}' — file may be corrupt or unsupported", request.SourceFilePath);
            throw;
        }
        catch (IOException ex)
        {   // File missing or inaccessible
            _logger.LogError(ex, "File not found or inaccessible: '{SourceFile}'", request.SourceFilePath);
            throw;
        }

        using (doc)
        {
            var pageCount = doc.Pages.Count;
            var pngDevice = new PngDevice(new Resolution(150));
            var pageStreams = new List<MemoryStream>(pageCount);
            try
            {
                try
                {
                    for (var i = 1; i <= pageCount; i++)
                    {
                        var ms = new MemoryStream();
                        pngDevice.Process(doc.Pages[i], ms);
                        ms.Position = 0;
                        pageStreams.Add(ms);
                    }
                }
                catch (PdfException ex)
                {   // Corrupt/unsupported format
                    _logger.LogError(ex, "Aspose.PDF failed to render a page from '{SourceFile}'", request.SourceFilePath);
                    throw;
                }

                try
                {
                    using var collection = new MagickImageCollection();
                    foreach (var stream in pageStreams)
                        collection.Add(new MagickImage(stream));

                    using var combined = collection.AppendVertically();
                    combined.SetAttribute("PageCount", pageCount.ToString());

                    using var result = new MemoryStream();
                    await combined.WriteAsync(result, MagickFormat.Png);
                    return result.ToArray();
                }
                catch (MagickResourceLimitErrorException ex)
                {  
                    // PDF too large
                    _logger.LogError(ex, "ImageMagick resource limit exceeded stitching '{SourceFile}' — PDF may be too large", request.SourceFilePath);
                    throw;
                }
                catch (MagickException ex)
                {
                    _logger.LogError(ex, "ImageMagick failed to stitch pages from '{SourceFile}'", request.SourceFilePath);
                    throw;
                }
            }
            finally
            {
                foreach (var ms in pageStreams)
                    ms.Dispose();
            }
        }
    }

    private void EnsurePdfLicenseConfigured()
    {
        if (_pdfLicenseConfigured) return;
        lock (PdfLicenseLock)
        {
            if (_pdfLicenseConfigured) return;
            if (!TryGetLicensePath(out var path)) { _pdfLicenseConfigured = true; return; }
            var license = new License();
            license.SetLicense(path);
            _pdfLicenseConfigured = true;
        }
    }

    private bool TryGetLicensePath(out string path)
    {
        path = _asposeOptions.LicensePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Aspose.PDF license path is not configured — running in evaluation mode (4-page limit applies).");
            return false;
        }
        if (!File.Exists(path))
        {
            _logger.LogWarning("Aspose.PDF license file not found at {LicensePath} — running in evaluation mode (4-page limit applies).", path);
            return false;
        }
        return true;
    }
}
