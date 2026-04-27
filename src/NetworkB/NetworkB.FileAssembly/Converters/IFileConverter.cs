namespace NetworkB.FileAssembly.Converters;

public interface IFileConverter
{
    bool CanConvert(string fromExtension, string toExtension);

    Task<byte[]> ConvertAsync(ConvertRequest request);
}
