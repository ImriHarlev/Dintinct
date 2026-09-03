using Aspose.Slides;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Converters;

public sealed class PresentationToPngConverter : IFileConverter
{
    private static readonly Lock LicenseLock = new();
    private static bool _licenseConfigured;

    private readonly ProxyConfigOptions _proxy;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<PresentationToPngConverter> _logger;

    public PresentationToPngConverter(
        IOptions<ProxyConfigOptions> proxy,
        IOptions<AsposeOptions> asposeOptions,
        ILogger<PresentationToPngConverter> logger)
    {
        _proxy = proxy.Value;
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    private static readonly HashSet<string> PresentationExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "ppt", "pptx" };

    public bool CanConvert(string fromExtension, string toExtension) =>
        toExtension.Equals("png", StringComparison.OrdinalIgnoreCase) &&
        PresentationExtensions.Contains(fromExtension);

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        EnsureLicenseConfigured();

        _logger.LogInformation("Converting {File} to a single scan-like PNG", request.SourceFilePath);

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
            var slideCount = presentation.Slides.Count;

            // Render each slide independently in parallel (Aspose read operations on separate
            // slides are safe to call concurrently when the Presentation is not being modified).
            var slideBytes = new byte[slideCount][];
            Parallel.For(0, slideCount, i =>
            {
                using var slideImage = presentation.Slides[i].GetImage(1f, 1f);
                using var ms = new MemoryStream();
                slideImage.Save(ms, Aspose.Slides.ImageFormat.Png);
                slideBytes[i] = ms.ToArray();
            });

            try
            {
                using var collection = new MagickImageCollection();
                foreach (var bytes in slideBytes)
                    collection.Add(new MagickImage(bytes));

                using var combined = collection.AppendVertically();
                using var resultStream = new MemoryStream();
                await combined.WriteAsync(resultStream, MagickFormat.Png);
                return resultStream.ToArray();
            }
            catch (MagickResourceLimitErrorException ex)
            {
                _logger.LogError(ex, "ImageMagick: resource limit exceeded stitching {SlideCount} slides from '{SourceFile}'",
                    slideCount, request.SourceFilePath);
                throw;
            }
            catch (MagickException ex)
            {
                _logger.LogError(ex, "ImageMagick error stitching slides from '{SourceFile}'",
                    request.SourceFilePath);
                throw;
            }
        }
    }

    private void EnsureLicenseConfigured()
    {
        if (_licenseConfigured)
            return;

        lock (LicenseLock)
        {
            if (_licenseConfigured)
                return;

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
