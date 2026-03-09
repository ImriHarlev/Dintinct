using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IProxyConfigRepository
{
    Task<ProxyConfiguration?> FindBySourceFormatAsync(string sourceFormat, CancellationToken ct = default);
    Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default);
}
