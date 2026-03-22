using Aspose.Words;
using Aspose.Words.Saving;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkA.Activities.HeavyProcessing.Splitters;

public sealed class DocxFileSplitter : IFileSplitter
{
    private static readonly Lock LicenseLock = new();
    private static bool _licenseConfigured;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<DocxFileSplitter> _logger;

    public DocxFileSplitter(IOptions<AsposeOptions> asposeOptions, ILogger<DocxFileSplitter> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    public bool CanSplit(string fileExtension)
    {
        return string.Equals(fileExtension, "docx", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        EnsureLicenseConfigured();

        var originalBytes = await File.ReadAllBytesAsync(request.SourceFilePath);
        if (!request.FileSizeLimitMb.HasValue)
        {
            return [originalBytes];
        }

        var maxChunkSizeBytes = request.FileSizeLimitMb.Value * 1024 * 1024;
        if (originalBytes.Length <= maxChunkSizeBytes)
        {
            return [originalBytes];
        }

        var document = new Document(request.SourceFilePath);
        if (document.PageCount <= 1)
        {
            return [originalBytes];
        }

        var chunks = new List<byte[]>();
        var startPage = 0;

        while (startPage < document.PageCount)
        {
            var bestPageCount = FindBestPageCount(document, startPage, maxChunkSizeBytes);
            chunks.Add(SavePages(document, startPage, bestPageCount));
            startPage += bestPageCount;
        }

        return chunks;
    }

    private static int FindBestPageCount(Document document, int startPage, int maxChunkSizeBytes)
    {
        var remainingPages = document.PageCount - startPage;
        var low = 1;
        var high = remainingPages;
        var bestPageCount = 1;

        while (low <= high)
        {
            var candidatePageCount = low + ((high - low) / 2);
            var candidateBytes = SavePages(document, startPage, candidatePageCount);

            if (candidateBytes.Length <= maxChunkSizeBytes)
            {
                bestPageCount = candidatePageCount;
                low = candidatePageCount + 1;
            }
            else
            {
                high = candidatePageCount - 1;
            }
        }

        return bestPageCount;
    }

    private static byte[] SavePages(Document document, int startPage, int pageCount)
    {
        var pageDocument = document.ExtractPages(startPage, pageCount);
        using var stream = new MemoryStream();
        pageDocument.Save(stream, new OoxmlSaveOptions(SaveFormat.Docx));
        return stream.ToArray();
    }

    private void EnsureLicenseConfigured()
    {
        if (_licenseConfigured)
        {
            return;
        }

        lock (LicenseLock)
        {
            if (_licenseConfigured)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_asposeOptions.LicensePath))
            {
                _logger.LogWarning("Aspose license path is not configured. Continuing without loading a license.");
                _licenseConfigured = true;
                return;
            }

            var license = new License();
            license.SetLicense(_asposeOptions.LicensePath);
            _licenseConfigured = true;
        }
    }
}
