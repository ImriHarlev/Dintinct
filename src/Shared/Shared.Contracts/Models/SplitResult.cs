namespace Shared.Contracts.Models;

public record SplitResult(
    string PackageType,
    string OriginalPackageName,
    int TotalChunks,
    IReadOnlyList<FileDescriptor> Files,
    IReadOnlyList<string> NestedArchives);
