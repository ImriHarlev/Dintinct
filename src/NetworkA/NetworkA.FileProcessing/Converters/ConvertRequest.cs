namespace NetworkA.FileProcessing.Converters;

public sealed record ConvertRequest(
    string SourceFilePath,
    string FromExtension,
    string ToExtension);
