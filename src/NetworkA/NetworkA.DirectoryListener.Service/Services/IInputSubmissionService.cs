using NetworkA.DirectoryListener.Service.Options;

namespace NetworkA.DirectoryListener.Service.Services;

public interface IInputSubmissionService
{
    Task<string> SubmitAsync(WatchedDirectoryOptions directory, string filePath, CancellationToken ct = default);
}
