using Shared.Contracts.Enums;

namespace NetworkA.DirectoryListener.Service.Options;

public class WatchedDirectoryOptions
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Filter { get; set; } = "*.*";
    public bool IncludeSubdirectories { get; set; }
    public string CallingSystemId { get; set; } = string.Empty;
    public string CallingSystemName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string TargetNetwork { get; set; } = string.Empty;
    public AnswerType AnswerType { get; set; } = AnswerType.FileSystem;
    public string? AnswerLocation { get; set; }
}
