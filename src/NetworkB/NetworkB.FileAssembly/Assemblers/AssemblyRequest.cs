namespace NetworkB.FileAssembly.Assemblers;

public sealed record AssemblyRequest(
    string OriginalFileExtension,
    string OutputPath,
    IReadOnlyList<AssemblyChunk> Chunks);

public sealed record AssemblyChunk(
    string Name,
    string Path,
    byte[] Content);
