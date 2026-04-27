namespace NetworkB.FileAssembly.Converters;

public sealed class DefaultFileConverter : IFileConverter
{
    public bool CanConvert(string fromExtension, string toExtension)
    {
        return false;
    }

    public async Task<byte[]> ConvertAsync(ConvertRequest request)
    {
        return await File.ReadAllBytesAsync(request.SourceFilePath);
    }
}
