namespace Shared.Contracts.Models;

public class ProxyConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = string.Empty;
    public IReadOnlyList<string> RequiredConversion { get; set; } = [];
    public int? FileSizeLimitMb { get; set; }
}
