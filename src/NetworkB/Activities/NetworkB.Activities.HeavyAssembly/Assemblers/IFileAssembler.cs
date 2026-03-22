namespace NetworkB.Activities.HeavyAssembly.Assemblers;

public interface IFileAssembler
{
    bool CanAssemble(string fileExtension);

    Task AssembleAsync(AssemblyRequest request);
}
