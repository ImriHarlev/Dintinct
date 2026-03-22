namespace NetworkB.Activities.HeavyAssembly.Assemblers;

public sealed class FileAssemblerFactory
{
    private readonly IEnumerable<IFileAssembler> _assemblers;
    private readonly DefaultFileAssembler _defaultAssembler;

    public FileAssemblerFactory(IEnumerable<IFileAssembler> assemblers, DefaultFileAssembler defaultAssembler)
    {
        _assemblers = assemblers;
        _defaultAssembler = defaultAssembler;
    }

    public IFileAssembler GetAssembler(string fileExtension)
    {
        return _assemblers.FirstOrDefault(assembler => assembler.CanAssemble(fileExtension)) ?? _defaultAssembler;
    }
}
