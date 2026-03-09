using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IProxyConfigCache
{
    Task<ProxyConfiguration?> GetAsync(string sourceFormat, CancellationToken ct = default);
    Task InvalidateAsync(string sourceFormat, CancellationToken ct = default);
}
