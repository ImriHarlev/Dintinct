using Shared.Contracts.Models;

namespace NetworkA.Activities.JobSetup.Interfaces;

public interface IProxyConfigRepository
{
    Task<ProxyConfiguration?> FindBySourceFormatAsync(string sourceFormat, CancellationToken ct = default);
    Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default);
}
