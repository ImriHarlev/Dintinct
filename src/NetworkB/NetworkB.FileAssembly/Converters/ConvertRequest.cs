namespace NetworkB.FileAssembly.Converters;

public sealed record ConvertRequest(
    string SourceFilePath,
    string FromExtension,
    string ToExtension);
