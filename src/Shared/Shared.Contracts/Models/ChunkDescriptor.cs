namespace Shared.Contracts.Models;

public record ChunkDescriptor(
    string Name,
    int Index,
    string Checksum);
