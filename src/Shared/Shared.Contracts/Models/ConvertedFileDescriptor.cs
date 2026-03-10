namespace Shared.Contracts.Models;

public record ConvertedFileDescriptor(
    string ConvertedRelativePath,
    IReadOnlyList<ChunkDescriptor> Chunks);
