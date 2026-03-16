namespace Shared.Contracts.Models;

public record AssembleFilesResult(
    IReadOnlyList<FileResult> FileResults,
    string AssemblyDir);
