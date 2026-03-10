namespace Shared.Contracts.Models;

public record FileDescriptor(
    string OriginalRelativePath,
    string OriginalFormat,
    IReadOnlyList<string> AppliedConversion,
    IReadOnlyList<ConvertedFileDescriptor> ConvertedFiles);
