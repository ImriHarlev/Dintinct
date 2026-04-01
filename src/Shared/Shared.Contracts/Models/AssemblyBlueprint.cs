using Shared.Contracts.Enums;

namespace Shared.Contracts.Models;

public class AssemblyBlueprint
{
    public string Id { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public string OriginalPackageName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string TargetNetwork { get; set; } = string.Empty;
    public string CallingSystemId { get; set; } = string.Empty;
    public string CallingSystemName { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public int TotalChunks { get; set; }
    public AnswerType AnswerType { get; set; }
    public string? AnswerLocation { get; set; }
    public List<FileDescriptor> Files { get; set; } = new();
    public List<string> NestedArchives { get; set; } = new();
    public HashSet<string> ReceivedChunkNames { get; set; } = new();
    public HashSet<string> UnsupportedChunkNames { get; set; } = new();
    public HashSet<string> HardFailedChunkNames { get; set; } = new();
    public string Status { get; set; } = string.Empty;
}
