using System.Text.Json;
using Shared.Contracts.Interfaces;
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

        var dir = Path.GetDirectoryName(payload.AnswerLocation);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        await File.WriteAllTextAsync(payload.AnswerLocation, json, ct);

        _logger.LogInformation("Status callback written to FileSystem at {AnswerLocation}", payload.AnswerLocation);
    }
}
