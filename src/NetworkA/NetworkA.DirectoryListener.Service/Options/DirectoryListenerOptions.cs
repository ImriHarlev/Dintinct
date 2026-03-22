namespace NetworkA.DirectoryListener.Service.Options;

public class DirectoryListenerOptions
{
    public const string SectionName = "DirectoryListener";

    public int PollIntervalSeconds { get; set; } = 10;
    public int StableForSeconds { get; set; } = 15;
    public int MaxConcurrency { get; set; } = 4;
    public int QueueCapacity { get; set; } = 10_000;
    public int ReadyCheckMaxAttempts { get; set; } = 10;
    public int ReadyCheckDelayMilliseconds { get; set; } = 500;
    public List<WatchedDirectoryOptions> Directories { get; set; } = [];
}
