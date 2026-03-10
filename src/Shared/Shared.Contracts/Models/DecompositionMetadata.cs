using Shared.Contracts.Enums;

namespace Shared.Contracts.Models;

public record DecompositionMetadata(
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
    IReadOnlyList<FileDescriptor> Files);
