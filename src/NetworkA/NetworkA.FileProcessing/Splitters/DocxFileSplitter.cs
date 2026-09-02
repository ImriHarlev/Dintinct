using Aspose.Words;
using Aspose.Words.Saving;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Shared.Infrastructure.Options;

namespace NetworkA.FileProcessing.Splitters;

public sealed class DocxFileSplitter : IFileSplitter
{
    // Single instance is shared across all calls.
    private static readonly OoxmlSaveOptions DocxSaveOptions = new(SaveFormat.Docx);

    // Single pooled manager buffers are rented per call and returned on Dispose, not GC'd 
    private static readonly Lock LicenseLock = new();

    // Fixes the double-checked lock: without volatile, the JIT can reorder the outer read, making the guard invisible to other threads
    private static volatile bool _licenseConfigured;
    private readonly AsposeOptions _asposeOptions;
    private readonly RecyclableMemoryStreamManager _streamManager;
    private readonly ILogger<DocxFileSplitter> _logger;

    public DocxFileSplitter(
        IOptions<AsposeOptions> asposeOptions,
        RecyclableMemoryStreamManager streamManager,
        ILogger<DocxFileSplitter> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _streamManager = streamManager;
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

        Document document;
        try
        {
            // Bytes already in memory from the size check
            document = new Document(new MemoryStream(originalBytes));
        }
        catch (UnsupportedFileFormatException ex)
        {
            _logger.LogError(ex, "Aspose.Words: unrecognised file format loading '{SourceFile}' for splitting",
                request.SourceFilePath);
            throw;
        }
        catch (FileCorruptedException ex)
        {
            _logger.LogError(ex, "Aspose.Words: corrupt or damaged DOCX '{SourceFile}'",
                request.SourceFilePath);
            throw;
        }
        catch (IncorrectPasswordException ex)
        {
            _logger.LogError(ex, "Aspose.Words: password-protected DOCX '{SourceFile}' cannot be split",
                request.SourceFilePath);
            throw;
        }

        if (document.PageCount <= 1)
        {
            return [originalBytes];
        }

        var chunks = new List<byte[]>();
        var startPage = 0;

        while (startPage < document.PageCount)
        {
            var (pageCount, data) = FindBestPages(document, startPage, maxChunkSizeBytes);
            chunks.Add(data);
            startPage += pageCount;
        }

        return chunks;
    }

    // Binary search for the largest page range that fits within maxChunkSizeBytes.
    // The winning stream is held open until the loop ends so we avoid a redundant
    // ExtractPages + Save that the old separate MeasurePages/SavePages pattern required.
    // DOCX is ZIP-based and requires a seekable stream, so CountingStream cannot be used.
    // Merged binary search + final encode into FindBestPages
    // The old structure called MeasurePages(encode → measure → discard stream) during search, then SavePages(encode again → return bytes) for the winner.
    // The winning page range was encoded twice. The new FindBestPages holds the winning RecyclableMemoryStream open through the search loop(disposing superseded streams back to the pool), 
    // then extracts bytes from it once at the end.Saves one full ExtractPages + Save round-trip per chunk — significant since both involve Aspose DOM cloning and ZIP serialization.
    private (int pageCount, byte[] data) FindBestPages(Document document, int startPage, int maxChunkSizeBytes)
    {
        var remainingPages = document.PageCount - startPage;
        var low = 1;
        var high = remainingPages;
        var bestPageCount = 1;
        RecyclableMemoryStream? winnerStream = null;

        while (low <= high)
        {
            var candidatePageCount = low + ((high - low) / 2);
            var stream = _streamManager.GetStream();
            document.ExtractPages(startPage, candidatePageCount).Save(stream, DocxSaveOptions);

            if (stream.Length <= maxChunkSizeBytes)
            {
                winnerStream?.Dispose();
                bestPageCount = candidatePageCount;
                winnerStream = stream;
                low = candidatePageCount + 1;
            }
            else
            {
                stream.Dispose();
                high = candidatePageCount - 1;
            }
        }

        if (winnerStream is not null)
        {
            using (winnerStream)
                return (bestPageCount, winnerStream.GetBuffer()[..(int)winnerStream.Length]);
        }

        // Edge: even a single page exceeds the limit — force it as a chunk anyway.
        using var fallback = _streamManager.GetStream();
        document.ExtractPages(startPage, 1).Save(fallback, DocxSaveOptions);
        return (1, fallback.GetBuffer()[..(int)fallback.Length]);
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
