using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.Dispatch.Activities;

public class WriteHardFailActivity
{
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<WriteHardFailActivity> _logger;

    public WriteHardFailActivity(
        IOptions<OutboxOptions> outboxOptions,
        ILogger<WriteHardFailActivity> logger)
    {
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task WriteHardFailAsync(string chunkName)
    {
        _logger.LogInformation("Writing hard-fail marker for chunk {ChunkName}", chunkName);

        var outboxPath = ResolveOutboxPath(chunkName);
        Directory.CreateDirectory(outboxPath);

        // Write a .HARDFAIL.txt file per data-model.md §3.1 naming convention
        var hardFailFileName = $"{chunkName}.HARDFAIL.txt";
        var hardFailPath = Path.Combine(outboxPath, hardFailFileName);
        await File.WriteAllTextAsync(hardFailPath, $"Hard fail for chunk {chunkName}");

        _logger.LogInformation("Hard-fail marker written: {HardFailPath}", hardFailPath);
    }

    private string ResolveOutboxPath(string chunkName) =>
        chunkName.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase)
            ? _outboxOptions.ManifestOutboxPath
            : _outboxOptions.DataOutboxPath;
}
