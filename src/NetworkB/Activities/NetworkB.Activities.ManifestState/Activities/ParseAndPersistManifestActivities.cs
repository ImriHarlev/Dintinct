using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkB.Activities.ManifestState.Interfaces;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Temporalio.Activities;

namespace NetworkB.Activities.ManifestState.Activities;

public class ParseAndPersistManifestActivities
{
    private readonly IAssemblyBlueprintRepository _repository;
    private readonly ILogger<ParseAndPersistManifestActivities> _logger;

    public ParseAndPersistManifestActivities(
        IAssemblyBlueprintRepository repository,
        ILogger<ParseAndPersistManifestActivities> logger)
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
            SourcePath = metadata.SourcePath,
            TargetPath = metadata.TargetPath,
            TargetNetwork = metadata.TargetNetwork,
            CallingSystemId = metadata.CallingSystemId,
            CallingSystemName = metadata.CallingSystemName,
            ExternalId = metadata.ExternalId,
            TotalChunks = metadata.TotalChunks,
            AnswerType = metadata.AnswerType,
            AnswerLocation = metadata.AnswerLocation,
            Files = metadata.Files.ToList(),
            NestedArchives = metadata.NestedArchives.ToList(),
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
        string SourcePath,
        string TargetPath,
        string TargetNetwork,
        string CallingSystemId,
        string CallingSystemName,
        string ExternalId,
        int TotalChunks,
        AnswerType AnswerType,
        string? AnswerLocation,
        IReadOnlyList<FileDescriptor> Files,
        IReadOnlyList<string> NestedArchives);
}
