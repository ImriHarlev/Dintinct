using Aspose.Pdf;
using Aspose.Pdf.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class PdfToDocxConverter : IFileConverter
{
    private static readonly Lock PdfLicenseLock = new();
    private static volatile bool _pdfLicenseConfigured;

    private readonly AsposeOptions _asposeOptions;
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<PdfToDocxConverter> _logger;

    public PdfToDocxConverter(
        IOptions<AsposeOptions> asposeOptions,
        RecyclableMemoryStreamManager streamManager,
        ILogger<PdfToDocxConverter> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _streamManager = streamManager;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("pdf", StringComparison.OrdinalIgnoreCase) &&
        toExtension.Equals("docx", StringComparison.OrdinalIgnoreCase);

    public Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsurePdfLicenseConfigured();

        _logger.LogInformation("Converting {File} to DOCX using Aspose.PDF", request.SourceFilePath);

        Document doc;
        try
        {
            doc = new Document(request.SourceFilePath);
        }
        catch (InvalidPasswordException ex)
        {
            _logger.LogError(ex, "Aspose.PDF: password-protected PDF '{SourceFile}' cannot be converted", request.SourceFilePath);
            throw;
        }
        catch (PdfException ex)
        {
            _logger.LogError(ex, "Aspose.PDF: corrupt or unsupported PDF '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Aspose.PDF: file not found or inaccessible '{SourceFile}'", request.SourceFilePath);
            throw;
        }

        using (doc)
        {
            var saveOptions = new DocSaveOptions
            {
                Format = DocSaveOptions.DocFormat.DocX,
                Mode = IsRTL(doc)
                    ? DocSaveOptions.RecognitionMode.Textbox
                    : DocSaveOptions.RecognitionMode.EnhancedFlow
            };

            using var stream = _streamManager.GetStream();
            doc.Save(stream, saveOptions);
            return Task.FromResult(stream.GetBuffer()[..(int)stream.Length]);
        }
    }

    private static bool IsRTL(Document pdfDoc, int pagesToScan = 3)
    {
        var absorber = new TextAbsorber();
        int pages = Math.Min(pagesToScan, pdfDoc.Pages.Count);

        for (int i = 1; i <= pages; i++)
            pdfDoc.Pages[i].Accept(absorber);

        return absorber.Text.Any(c =>
            (c >= 0x0590 && c <= 0x05FF) ||  // Hebrew
            (c >= 0x0600 && c <= 0x06FF) ||  // Arabic / Persian
            (c >= 0x0700 && c <= 0x074F));   // Syriac           
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
