namespace NetworkA.FileProcessing.Splitters;

public interface IFileSplitter
{
    bool CanSplit(string fileExtension);

    Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request);
}
