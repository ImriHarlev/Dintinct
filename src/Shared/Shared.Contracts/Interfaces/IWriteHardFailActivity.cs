namespace Shared.Contracts.Interfaces;

public interface IWriteHardFailActivity
{
    Task WriteHardFailAsync(string jobId, string chunkName);
}
