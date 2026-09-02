using Aspose.Words;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

public sealed class DocxToPdfConverter : IFileConverter
{
    private static readonly Lock LicenseLock = new();
    private static volatile bool _licenseConfigured;

    private readonly AsposeOptions _asposeOptions;
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<DocxToPdfConverter> _logger;

    public DocxToPdfConverter(
        IOptions<AsposeOptions> asposeOptions,
        RecyclableMemoryStreamManager streamManager,
        ILogger<DocxToPdfConverter> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _streamManager = streamManager;
        _logger = logger;
    }

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("docx", StringComparison.OrdinalIgnoreCase) &&
        toExtension.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsureLicenseConfigured();

        _logger.LogInformation("Converting {File} (DOCX) to PDF using Aspose.Words", request.SourceFilePath);

        Document doc;
        try
        {
            doc = new Document(request.SourceFilePath);
        }
        catch (UnsupportedFileFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Words: unrecognised file format '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (FileCorruptedException ex)
        {
            _logger.LogError(ex, "Aspose.Words: corrupt or damaged DOCX '{SourceFile}'", request.SourceFilePath);
            throw;
        }
        catch (IncorrectPasswordException ex)
        {
            _logger.LogError(ex, "Aspose.Words: password-protected DOCX '{SourceFile}' cannot be converted", request.SourceFilePath);
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Aspose.Words: file not found or inaccessible '{SourceFile}'", request.SourceFilePath);
            throw;
        }

        using var stream = _streamManager.GetStream();
        doc.Save(stream, SaveFormat.Pdf);
        return Task.FromResult(stream.GetBuffer()[..(int)stream.Length]);
    }

    private void EnsureLicenseConfigured()
    {
        if (_licenseConfigured) return;
        lock (LicenseLock)
        {
            if (_licenseConfigured) return;
            if (string.IsNullOrWhiteSpace(_asposeOptions.LicensePath))
            {
                _logger.LogWarning("Aspose license path is not configured — running in evaluation mode (watermark applies).");
                _licenseConfigured = true;
                return;
            }
            if (!File.Exists(_asposeOptions.LicensePath))
            {
                _logger.LogWarning("Aspose license file not found at {LicensePath} — running in evaluation mode (watermark applies).", _asposeOptions.LicensePath);
                _licenseConfigured = true;
                return;
            }
            var license = new License();
            license.SetLicense(_asposeOptions.LicensePath);
            _licenseConfigured = true;
        }
    }
}
