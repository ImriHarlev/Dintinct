namespace Shared.Contracts.Models;

public class ProxyConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = string.Empty;
    public string RequiredConversion { get; set; } = string.Empty;
    public int? FileSizeLimitMb { get; set; }
}
