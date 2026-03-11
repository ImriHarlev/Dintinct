using System.Text;
using NetworkB.Activities.Reporting.Interfaces;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;

namespace NetworkB.Activities.Reporting.Services;

public class CsvReportWriter : ICsvReportWriter
{
    public async Task WriteAsync(string outputPath, IReadOnlyList<FileResult> results, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("status,dir_path");

        foreach (var result in results)
        {
            var status = result.Status switch
            {
                FileTransferStatus.Completed => "COMPLETED",
                FileTransferStatus.Failed => "FAILED",
                FileTransferStatus.NotSupported => "NOT_SUPPORTED",
                _ => "FAILED"
            };

            sb.AppendLine($"{status},{result.DirPath}");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);
    }
}
