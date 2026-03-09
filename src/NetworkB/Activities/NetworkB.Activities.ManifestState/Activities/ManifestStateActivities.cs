using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.ManifestState.Activities;

public class ManifestStateActivities
{
    private readonly IAssemblyBlueprintRepository _repository;
    private readonly ILogger<ManifestStateActivities> _logger;

    public ManifestStateActivities(
        IAssemblyBlueprintRepository repository,
        ILogger<ManifestStateActivities> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [Activity]
    public async Task<AssemblyBlueprint> ParseAndPersistManifestAsync(string manifestFilePath)
    {
        _logger.LogInformation("Parsing manifest from {ManifestFilePath}", manifestFilePath);

        var json = await File.ReadAllTextAsync(manifestFilePath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        // Deserialize the hierarchical manifest matching data-model.md §3.2 schema
        var metadata = JsonSerializer.Deserialize<ManifestDocument>(json, options)
            ?? throw new InvalidOperationException($"Failed to parse manifest at {manifestFilePath}");

        var blueprint = new AssemblyBlueprint
        {
            Id = metadata.JobId,
            PackageType = metadata.PackageType,
            OriginalPackageName = metadata.OriginalPackageName,
            TargetPath = metadata.TargetPath,
            TotalChunks = metadata.TotalChunks,
            AnswerType = metadata.AnswerType,
            AnswerLocation = metadata.AnswerLocation,
            Files = metadata.Files.ToList(),
            ReceivedChunkNames = new HashSet<string>(),
            UnsupportedChunkNames = new HashSet<string>(),
            HardFailedChunkNames = new HashSet<string>(),
            Status = "Aggregating",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _repository.UpsertAsync(blueprint);

        _logger.LogInformation("Blueprint persisted for job {JobId} with {TotalChunks} expected chunks",
            blueprint.Id, blueprint.TotalChunks);

        return blueprint;
    }

    // Intermediate deserialization type matching the manifest JSON schema
    private record ManifestDocument(
        string JobId,
        string PackageType,
        string OriginalPackageName,
        string TargetPath,
        int TotalChunks,
        AnswerType AnswerType,
        string? AnswerLocation,
        IReadOnlyList<FileDescriptor> Files);
}
