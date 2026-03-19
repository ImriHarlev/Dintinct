namespace NetworkA.Activities.HeavyProcessing.Splitters;

public interface IFileSplitter
{
    bool CanSplit(string fileExtension);

    Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request);
}
