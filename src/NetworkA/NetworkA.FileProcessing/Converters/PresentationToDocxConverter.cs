using Aspose.Slides;
using Aspose.Words;
using Aspose.Words.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

/// <summary>
/// Converts PPT/PPTX to a DOCX where each page contains the corresponding slide
/// rendered as a full-page image, preserving the visual layout of every slide.
/// </summary>
public sealed class PresentationToDocxConverter : IFileConverter
{
    private static readonly Lock SlidesLicenseLock = new();
    private static bool _slidesLicenseConfigured;

    private readonly ProxyConfigOptions _proxy;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<PresentationToDocxConverter> _logger;

    public PresentationToDocxConverter(
        IOptions<ProxyConfigOptions> proxy,
        IOptions<AsposeOptions> asposeOptions,
        ILogger<PresentationToDocxConverter> logger)
    {
        _proxy = proxy.Value;
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    private static readonly HashSet<string> PresentationExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "ppt", "pptx" };

    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("docx", StringComparison.OrdinalIgnoreCase) &&
        PresentationExtensions.Contains(fromExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsureSlidesLicenseConfigured();

        _logger.LogInformation("Converting {File} to DOCX (slide-per-page)", request.SourceFilePath);

        Presentation presentation;
        try
        {
            presentation = new Presentation(request.SourceFilePath);
        }
        catch (PptUnsupportedFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Slides: unsupported or unrecognised format loading '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
        catch (PptxReadException ex)
        {
            _logger.LogError(ex, "Aspose.Slides: corrupt or unreadable presentation '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }

        using (presentation)
        {
            var wordDoc = new Document();
            var builder = new DocumentBuilder(wordDoc);

            // Read page dimensions before any content manipulation so the builder
            // cursor remains valid throughout the loop.
            var pageWidth = wordDoc.FirstSection.PageSetup.PageWidth;
            var pageHeight = wordDoc.FirstSection.PageSetup.PageHeight;

            for (var i = 0; i < presentation.Slides.Count; i++)
            {
                var slide = presentation.Slides[i];

            using var slideImage = slide.GetImage(3f, 3f);
            using var ms = new MemoryStream();
            slideImage.Save(ms, Aspose.Slides.ImageFormat.Png);
            ms.Position = 0;

                builder.InsertImage(ms,
                    RelativeHorizontalPosition.Page, 0,
                    RelativeVerticalPosition.Page, 0,
                    pageWidth,
                    pageHeight,
                    WrapType.None);

                if (i < presentation.Slides.Count - 1)
                    builder.InsertBreak(BreakType.PageBreak);
            }

            try
            {
                using var resultStream = new MemoryStream();
                wordDoc.Save(resultStream, Aspose.Words.SaveFormat.Docx);
                return await Task.FromResult(resultStream.ToArray());
            }
            catch (UnsupportedFileFormatException ex)
            {
                _logger.LogError(ex, "Aspose.Words: error saving presentation '{SourceFile}' as DOCX",
                    request.SourceFilePath);
                throw;
            }
        }
    }

    private void EnsureSlidesLicenseConfigured()
    {
        if (_slidesLicenseConfigured)
            return;

        lock (SlidesLicenseLock)
        {
            if (_slidesLicenseConfigured)
                return;

            if (string.IsNullOrWhiteSpace(_asposeOptions.LicensePath))
            {
                _logger.LogWarning("Aspose license path is not configured. Continuing without loading a license.");
                _slidesLicenseConfigured = true;
                return;
            }

            if (!File.Exists(_asposeOptions.LicensePath))
            {
                _logger.LogWarning(
                    "Aspose license file not found at {LicensePath}. Continuing without a license.",
                    _asposeOptions.LicensePath);
                _slidesLicenseConfigured = true;
                return;
            }

            var slidesLicense = new Aspose.Slides.License();
            slidesLicense.SetLicense(_asposeOptions.LicensePath);
            _slidesLicenseConfigured = true;
        }
    }
}
