namespace NetworkB.FileAssembly.Assemblers;

public interface IFileAssembler
{
    bool CanAssemble(string fileExtension);

    Task AssembleAsync(AssemblyRequest request);
}
