using Shared.Contracts.Enums;

namespace Shared.Contracts.Payloads;

public record IngestionRequestPayload(
    string CallingSystemId,
    string CallingSystemName,
    string SourcePath,
    string TargetPath,
    string TargetNetwork,
    string ExternalId,
    AnswerType AnswerType,
    string? AnswerLocation);
