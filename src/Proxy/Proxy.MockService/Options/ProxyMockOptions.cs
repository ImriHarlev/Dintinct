namespace Proxy.MockService.Options;

public class ProxyMockOptions
{
    public string ProxyExchange { get; set; } = "proxy.events";
    public string RoutingKey { get; set; } = "file.arrived";
    public List<InboxConfig> Inboxes { get; set; } = [];
}
