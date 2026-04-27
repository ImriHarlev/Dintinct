using Aspose.Words;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;

namespace NetworkB.FileAssembly.Assemblers;

public sealed class DocsAssembler : IFileAssembler
{
    private static readonly Lock LicenseLock = new();
    private static bool _licenseConfigured;
    private readonly AsposeOptions _asposeOptions;
    private readonly ILogger<DocsAssembler> _logger;

    public DocsAssembler(IOptions<AsposeOptions> asposeOptions, ILogger<DocsAssembler> logger)
    {
        _asposeOptions = asposeOptions.Value;
        _logger = logger;
    }

    public bool CanAssemble(string fileExtension)
    {
        return string.Equals(fileExtension, "docx", StringComparison.OrdinalIgnoreCase);
    }

    public async Task AssembleAsync(AssemblyRequest request)
    {
        EnsureLicenseConfigured();

        if (request.Chunks.Count == 0)
        {
            await File.WriteAllBytesAsync(request.OutputPath, []);
            return;
        }

        using var firstStream = new MemoryStream(request.Chunks[0].Content, writable: false);
        var assembledDocument = new Document(firstStream);

        for (var i = 1; i < request.Chunks.Count; i++)
        {
            using var chunkStream = new MemoryStream(request.Chunks[i].Content, writable: false);
            var chunkDocument = new Document(chunkStream);
            assembledDocument.AppendDocument(chunkDocument, ImportFormatMode.KeepSourceFormatting);
        }

        await using var outputStream = File.Create(request.OutputPath);
        assembledDocument.Save(outputStream, SaveFormat.Docx);
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
