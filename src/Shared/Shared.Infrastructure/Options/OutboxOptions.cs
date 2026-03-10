namespace Shared.Infrastructure.Options;

public class OutboxOptions
{
    public string DataOutboxPath { get; set; } = string.Empty;
    public string ManifestOutboxPath { get; set; } = string.Empty;
}
