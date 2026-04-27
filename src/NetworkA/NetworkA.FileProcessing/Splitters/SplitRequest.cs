namespace NetworkA.FileProcessing.Splitters;

public sealed record SplitRequest(
    string SourceFilePath,
    int? FileSizeLimitMb);
