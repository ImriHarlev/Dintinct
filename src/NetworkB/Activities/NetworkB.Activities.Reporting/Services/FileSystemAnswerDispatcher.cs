using System.Text.Json;
using NetworkB.Activities.Reporting.Interfaces;
using Shared.Contracts.Payloads;

namespace NetworkB.Activities.Reporting.Services;

public class FileSystemAnswerDispatcher : IAnswerDispatcher
{
    private readonly ILogger<FileSystemAnswerDispatcher> _logger;

    public FileSystemAnswerDispatcher(ILogger<FileSystemAnswerDispatcher> logger)
    {
        _logger = logger;
    }

    public async Task DispatchAsync(StatusCallbackPayload payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.AnswerLocation))
            throw new InvalidOperationException("AnswerLocation must be set for FileSystem dispatch");

        Directory.CreateDirectory(payload.AnswerLocation);

        var fileName = $"{payload.ExternalId}.json";
        var filePath = Path.Combine(payload.AnswerLocation, fileName);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json, ct);

        _logger.LogInformation("Status callback written to FileSystem at {FilePath}", filePath);
    }
}
