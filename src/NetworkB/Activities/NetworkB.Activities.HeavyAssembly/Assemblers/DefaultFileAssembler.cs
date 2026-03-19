namespace NetworkB.Activities.HeavyAssembly.Assemblers;

public sealed class DefaultFileAssembler : IFileAssembler
{
    public bool CanAssemble(string fileExtension)
    {
        return false;
    }

    public async Task AssembleAsync(AssemblyRequest request)
    {
        await using var outputStream = File.Create(request.OutputPath);

        foreach (var chunk in request.Chunks)
        {
            await outputStream.WriteAsync(chunk.Content);
        }
    }
}
