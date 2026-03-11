using Shared.Contracts.Models;

namespace NetworkB.Activities.Reporting.Interfaces;

public interface ICsvReportWriter
{
    Task WriteAsync(string outputPath, IReadOnlyList<FileResult> results, CancellationToken ct = default);
}
