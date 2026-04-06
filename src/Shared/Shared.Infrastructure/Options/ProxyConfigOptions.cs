using Shared.Contracts.Models;

namespace Shared.Infrastructure.Options;

public class ProxyConfigOptions
{
    public List<ProxyConfiguration> Configurations { get; set; } = [];
}
