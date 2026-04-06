namespace Proxy.MockService.Options;

public class InboxConfig
{
    public string InboxPath { get; set; } = string.Empty;
    public string OutboxPath { get; set; } = string.Empty;
    public string FilePattern { get; set; } = "*.*";
    public LatencyOptions Latency { get; set; } = new();
    public SimulationOptions Simulation { get; set; } = new();
}
