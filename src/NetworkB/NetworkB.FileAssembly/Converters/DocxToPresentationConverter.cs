using Aspose.Slides;
using Aspose.Slides.Export;
using Aspose.Words.Saving;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

/// <summary>
/// Converts a DOCX (whose pages are rendered slide images produced by NetworkA's
/// PresentationToDocxConverter) back to PPT or PPTX by rendering each DOCX page as a PNG
/// with Aspose.Words and embedding it as a full-page slide in a new Aspose.Slides
/// presentation.
/// </summary>
public sealed class DocxToPresentationConverter : IFileConverter
{
    private static readonly Lock WordsLicenseLock = new();
    private static volatile bool _wordsLicenseConfigured;

    private static readonly Lock SlidesLicenseLock = new();
    private static volatile bool _slidesLicenseConfigured;

    private readonly ProxyConfigOptions _proxy;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<DocxToPresentationConverter> _logger;

    public DocxToPresentationConverter(
        IOptions<ProxyConfigOptions> proxy,
        IOptions<AsposeOptions> asposeOptions,
        ILogger<DocxToPresentationConverter> logger)
    {
        _proxy = proxy.Value;
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    private static readonly HashSet<string> PresentationExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "ppt", "pptx" };

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("docx", StringComparison.OrdinalIgnoreCase) &&
        PresentationExtensions.Contains(toExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsureWordsLicenseConfigured();
        EnsureSlidesLicenseConfigured();

        _logger.LogInformation(
            "Converting {File} (DOCX) to {TargetFormat} presentation",
            request.SourceFilePath, request.ToExtension);

        Aspose.Words.Document wordDoc;
        try
        {
            wordDoc = new Aspose.Words.Document(request.SourceFilePath);
        }
        catch (Aspose.Words.UnsupportedFileFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Words: unrecognised file format loading '{SourceFile}' as DOCX",
                request.SourceFilePath);
            throw;
        }
        catch (Aspose.Words.FileCorruptedException ex)
        {
            _logger.LogError(ex, "Aspose.Words: corrupt or damaged DOCX '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
        catch (Aspose.Words.IncorrectPasswordException ex)
        {
            _logger.LogError(ex, "Aspose.Words: password-protected DOCX '{SourceFile}' cannot be converted",
                request.SourceFilePath);
            throw;
        }

        using var presentation = new Presentation();
        presentation.Slides.RemoveAt(0);

        var slideWidth = presentation.SlideSize.Size.Width;
        var slideHeight = presentation.SlideSize.Size.Height;

        for (var pageIndex = 0; pageIndex < wordDoc.PageCount; pageIndex++)
        {
            var imageOptions = new ImageSaveOptions(Aspose.Words.SaveFormat.Png)
            {
                PageSet = new PageSet(pageIndex),
                Resolution = 300
            };

            using var pageStream = new MemoryStream();
            wordDoc.Save(pageStream, imageOptions);
            pageStream.Position = 0;

            var slide = presentation.Slides.AddEmptySlide(presentation.LayoutSlides[0]);
            var ppImage = presentation.Images.AddImage(pageStream);
            slide.Shapes.AddPictureFrame(ShapeType.Rectangle, 0, 0, slideWidth, slideHeight, ppImage);
        }

        var saveFormat = request.ToExtension.Equals("pptx", StringComparison.OrdinalIgnoreCase)
            ? SaveFormat.Pptx
            : SaveFormat.Ppt;

        try
        {
            using var resultStream = new MemoryStream();
            presentation.Save(resultStream, saveFormat);
            return await Task.FromResult(resultStream.ToArray());
        }
        catch (PptUnsupportedFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Slides: unsupported format saving '{SourceFile}' reconstruction as {TargetFormat}",
                request.SourceFilePath, request.ToExtension);
            throw;
        }
    }

    private void EnsureWordsLicenseConfigured()
    {
        if (_wordsLicenseConfigured) return;
        lock (WordsLicenseLock)
        {
            if (_wordsLicenseConfigured) return;
            if (!TryGetLicensePath(out var path)) { _wordsLicenseConfigured = true; return; }
            var license = new Aspose.Words.License();
            license.SetLicense(path);
            _wordsLicenseConfigured = true;
        }
    }

    private void EnsureSlidesLicenseConfigured()
    {
        if (_slidesLicenseConfigured) return;
        lock (SlidesLicenseLock)
        {
            if (_slidesLicenseConfigured) return;
            if (!TryGetLicensePath(out var path)) { _slidesLicenseConfigured = true; return; }
            var license = new Aspose.Slides.License();
            license.SetLicense(path);
            _slidesLicenseConfigured = true;
        }
    }

    private bool TryGetLicensePath(out string path)
    {
        path = _asposeOptions.LicensePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Aspose license path is not configured. Continuing without loading a license.");
            return false;
        }
        if (!File.Exists(path))
        {
            _logger.LogWarning("Aspose license file not found at {LicensePath}. Continuing without a license.", path);
            return false;
        }
        return true;
    }
}
