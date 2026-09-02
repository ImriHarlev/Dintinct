using Aspose.Slides;
using Aspose.Slides.Export;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Converters;

/// <summary>
/// Converts a stacked-PNG (produced by NetworkA's PresentationToPngConverter) back to
/// PPT or PPTX by embedding it as a single full-page slide in a new Aspose.Slides
/// presentation.
///
/// This converter is the back-conversion path for presentation formats whose
/// RequiredConversion in ProxyConfig contains "PNG" (e.g. pptx: ["PNG"]).
/// For the DOCX path (pptx: ["DOCX"]) use DocxToPresentationConverter instead.
/// </summary>
public sealed class PngToPresentationConverter : IFileConverter
{
    private static readonly Lock LicenseLock = new();
    private static volatile bool _licenseConfigured;

    private readonly ProxyConfigOptions _proxy;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<PngToPresentationConverter> _logger;

    public PngToPresentationConverter(
        IOptions<ProxyConfigOptions> proxy,
        IOptions<AsposeOptions> asposeOptions,
        ILogger<PngToPresentationConverter> logger)
    {
        _proxy = proxy.Value;
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    private static readonly HashSet<string> PresentationExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "ppt", "pptx" };

    public bool CanConvert(string fromExtension, string toExtension) =>
        fromExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        PresentationExtensions.Contains(toExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsureLicenseConfigured();

        _logger.LogInformation(
            "Converting PNG to {TargetFormat} presentation",
            request.ToExtension);

        try
        {
            using var presentation = new Presentation();
            presentation.Slides.RemoveAt(0);

            var slideWidth = presentation.SlideSize.Size.Width;
            var slideHeight = presentation.SlideSize.Size.Height;

            using var imageStream = File.OpenRead(request.SourceFilePath);
            var slide = presentation.Slides.AddEmptySlide(presentation.LayoutSlides[0]);
            var ppImage = presentation.Images.AddImage(imageStream);
            slide.Shapes.AddPictureFrame(ShapeType.Rectangle, 0, 0, slideWidth, slideHeight, ppImage);

            var saveFormat = request.ToExtension.Equals("pptx", StringComparison.OrdinalIgnoreCase)
                ? SaveFormat.Pptx
                : SaveFormat.Ppt;

            using var resultStream = new MemoryStream();
            presentation.Save(resultStream, saveFormat);
            return await Task.FromResult(resultStream.ToArray());
        }
        catch (PptUnsupportedFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Slides: unsupported format saving '{SourceFile}' as {TargetFormat}",
                request.SourceFilePath, request.ToExtension);
            throw;
        }
        catch (PptxReadException ex)
        {
            _logger.LogError(ex, "Aspose.Slides: error building presentation from '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
    }

    private void EnsureLicenseConfigured()
    {
        if (_licenseConfigured) return;
        lock (LicenseLock)
        {
            if (_licenseConfigured) return;

            if (string.IsNullOrWhiteSpace(_asposeOptions.LicensePath))
            {
                _logger.LogWarning("Aspose license path is not configured. Continuing without loading a license.");
                _licenseConfigured = true;
                return;
            }

            if (!File.Exists(_asposeOptions.LicensePath))
            {
                _logger.LogWarning(
                    "Aspose license file not found at {LicensePath}. Continuing without a license.",
                    _asposeOptions.LicensePath);
                _licenseConfigured = true;
                return;
            }

            var license = new License();
            license.SetLicense(_asposeOptions.LicensePath);
            _licenseConfigured = true;
        }
    }
}
