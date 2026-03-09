namespace Shared.Contracts.Models;

public record FileDescriptor(
    string OriginalRelativePath,
    string OriginalFormat,
    string AppliedConversion,
    IReadOnlyList<ConvertedFileDescriptor> ConvertedFiles);
