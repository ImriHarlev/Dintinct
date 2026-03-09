using Shared.Contracts.Enums;

namespace Shared.Contracts.Models;

public record DecompositionMetadata(
    string JobId,
    string PackageType,
    string OriginalPackageName,
    string TargetPath,
    int TotalChunks,
    AnswerType AnswerType,
    string? AnswerLocation,
    IReadOnlyList<FileDescriptor> Files);
