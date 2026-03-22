namespace NetworkA.Activities.HeavyProcessing.Splitters;

public sealed record SplitRequest(
    string SourceFilePath,
    int? FileSizeLimitMb);
