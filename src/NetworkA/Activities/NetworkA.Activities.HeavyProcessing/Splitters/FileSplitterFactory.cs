namespace NetworkA.Activities.HeavyProcessing.Splitters;

public sealed class FileSplitterFactory
{
    private readonly IEnumerable<IFileSplitter> _splitters;
    private readonly DefaultFileSplitter _defaultSplitter;

    public FileSplitterFactory(IEnumerable<IFileSplitter> splitters, DefaultFileSplitter defaultSplitter)
    {
        _splitters = splitters;
        _defaultSplitter = defaultSplitter;
    }

    public IFileSplitter GetSplitter(string fileExtension)
    {
        return _splitters.FirstOrDefault(splitter => splitter.CanSplit(fileExtension)) ?? _defaultSplitter;
    }
}
