using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Contracts.Models;
using Shared.Infrastructure.Options;
using Temporalio.Activities;

namespace NetworkA.Activities.Manifest.Activities;

public class ManifestActivities
{
    private readonly OutboxOptions _outboxOptions;
    private readonly ILogger<ManifestActivities> _logger;

    public ManifestActivities(
        IOptions<OutboxOptions> outboxOptions,
        ILogger<ManifestActivities> logger)
    {
        _outboxOptions = outboxOptions.Value;
        _logger = logger;
    }

    [Activity]
    public async Task WriteManifestAsync(DecompositionMetadata metadata)
    {
        Directory.CreateDirectory(_outboxOptions.ManifestOutboxPath);

        var manifestPath = Path.Combine(_outboxOptions.ManifestOutboxPath, $"{metadata.JobId}_manifest.json");

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(metadata, options);
        await File.WriteAllTextAsync(manifestPath, json);

        _logger.LogInformation("Manifest written to {ManifestPath} for job {JobId}", manifestPath, metadata.JobId);
    }
}
