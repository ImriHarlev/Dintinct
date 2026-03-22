using Microsoft.Extensions.DependencyInjection;
using NetworkA.DirectoryListener.Service.Options;
using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Temporalio.Client;

namespace NetworkA.DirectoryListener.Service.Services;

public class InputSubmissionService : IInputSubmissionService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ITemporalClient _temporalClient;
    private readonly ILogger<InputSubmissionService> _logger;

    public InputSubmissionService(
        IServiceScopeFactory serviceScopeFactory,
        ITemporalClient temporalClient,
        ILogger<InputSubmissionService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _temporalClient = temporalClient;
        _logger = logger;
    }

    public async Task<string> SubmitAsync(WatchedDirectoryOptions directory, string filePath, CancellationToken ct = default)
    {
        var payload = BuildPayload(directory, filePath);
        ValidatePayload(payload);

        var jobId = Guid.NewGuid().ToString();
        var workflowId = $"decomposition-{jobId}";
        var utcNow = DateTime.UtcNow;

        var job = new Job
        {
            Id = jobId,
            ExternalId = payload.ExternalId,
            OriginalRequest = payload,
            Status = "Processing",
            TemporalWorkflowId = workflowId,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };

        using var scope = _serviceScopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        await jobRepository.UpsertAsync(job, ct);

        await _temporalClient.StartWorkflowAsync(
            "DecompositionWorkflow",
            [payload],
            new WorkflowOptions(workflowId, "decomposition-workflow"));

        _logger.LogInformation(
            "Started workflow {WorkflowId} for file {FilePath} from listener {ListenerName}",
            workflowId,
            filePath,
            directory.Name);

        return jobId;
    }

    private static IngestionRequestPayload BuildPayload(WatchedDirectoryOptions directory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required", nameof(filePath));

        return new IngestionRequestPayload(
            directory.CallingSystemId,
            directory.CallingSystemName,
            filePath,
            directory.TargetPath,
            directory.TargetNetwork,
            Path.GetFileName(filePath),
            directory.AnswerType,
            directory.AnswerLocation);
    }

    private static void ValidatePayload(IngestionRequestPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.CallingSystemId))
            throw new InvalidOperationException("CallingSystemId is required for the directory listener.");

        if (string.IsNullOrWhiteSpace(payload.CallingSystemName))
            throw new InvalidOperationException("CallingSystemName is required for the directory listener.");

        if (string.IsNullOrWhiteSpace(payload.SourcePath))
            throw new InvalidOperationException("SourcePath is required for the directory listener.");

        if (string.IsNullOrWhiteSpace(payload.TargetPath))
            throw new InvalidOperationException("TargetPath is required for the directory listener.");

        if (string.IsNullOrWhiteSpace(payload.TargetNetwork))
            throw new InvalidOperationException("TargetNetwork is required for the directory listener.");

        if (string.IsNullOrWhiteSpace(payload.ExternalId))
            throw new InvalidOperationException("ExternalId is required for the directory listener.");

        if (!Enum.IsDefined(payload.AnswerType))
            throw new InvalidOperationException("AnswerType must be a valid enum value.");

        if (payload.AnswerType == AnswerType.FileSystem)
        {
            if (string.IsNullOrWhiteSpace(payload.AnswerLocation))
                throw new InvalidOperationException("AnswerLocation is required when AnswerType is FileSystem.");

            if (!string.IsNullOrEmpty(Path.GetExtension(payload.AnswerLocation)))
                throw new InvalidOperationException("AnswerLocation must be a directory path, not a file path.");
        }
    }
}
