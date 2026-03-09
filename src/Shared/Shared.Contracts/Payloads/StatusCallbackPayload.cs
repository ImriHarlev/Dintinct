using Shared.Contracts.Enums;

namespace Shared.Contracts.Payloads;

public record StatusCallbackPayload(
    string CallingSystemId,
    string CallingSystemName,
    string SourcePath,
    string TargetPath,
    string TargetNetwork,
    string ExternalId,
    AnswerType AnswerType,
    string? AnswerLocation,
    string JobId,
    DateTime UpdateDate,
    int JobCount,
    string OrigJobId,
    JobStatus JobStatus);
