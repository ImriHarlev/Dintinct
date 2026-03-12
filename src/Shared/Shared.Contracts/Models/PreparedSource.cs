namespace Shared.Contracts.Models;

public record PreparedSource(
    string WorkDir,
    string PackageType,
    string OriginalPackageName,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> NestedArchives);
