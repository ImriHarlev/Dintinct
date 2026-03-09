using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface ICsvReportWriter
{
    Task WriteAsync(string outputPath, IReadOnlyList<FileResult> results, CancellationToken ct = default);
}
