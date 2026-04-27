namespace NetworkA.FileProcessing.Splitters;

public sealed class DefaultFileSplitter : IFileSplitter
{
    public bool CanSplit(string fileExtension)
    {
        return false;
    }

    public async Task<IReadOnlyList<byte[]>> SplitAsync(SplitRequest request)
    {
        var fileBytes = await File.ReadAllBytesAsync(request.SourceFilePath);
        var chunkSizeBytes = request.FileSizeLimitMb.HasValue
            ? request.FileSizeLimitMb.Value * 1024 * 1024
            : fileBytes.Length;

        var chunkCount = (int)Math.Ceiling((double)fileBytes.Length / chunkSizeBytes);
        if (chunkCount < 1)
        {
            chunkCount = 1;
        }

        var chunks = new List<byte[]>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkSizeBytes;
            var length = Math.Min(chunkSizeBytes, fileBytes.Length - offset);
            chunks.Add(fileBytes.AsSpan(offset, length).ToArray());
        }

        return chunks;
    }
}
