using Shared.Contracts.Models;

namespace NetworkA.Activities.JobSetup.Interfaces;

public interface IProxyConfigCache
{
    Task<ProxyConfiguration?> GetAsync(string sourceFormat, CancellationToken ct = default);
    Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default);
    Task InvalidateAsync(string sourceFormat, CancellationToken ct = default);
}
