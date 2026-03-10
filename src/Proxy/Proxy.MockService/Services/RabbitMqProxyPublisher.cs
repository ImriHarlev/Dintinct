using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Proxy.MockService.Options;
using Shared.Infrastructure.Options;

namespace Proxy.MockService.Services;

public class RabbitMqProxyPublisher
{
    private readonly RabbitMqOptions _rabbitMqOptions;
    private readonly ProxyMockOptions _proxyMockOptions;
    private readonly ILogger<RabbitMqProxyPublisher> _logger;

    public RabbitMqProxyPublisher(
        IOptions<RabbitMqOptions> rabbitMqOptions,
        IOptions<ProxyMockOptions> proxyMockOptions,
        ILogger<RabbitMqProxyPublisher> logger)
    {
        _rabbitMqOptions = rabbitMqOptions.Value;
        _proxyMockOptions = proxyMockOptions.Value;
        _logger = logger;
    }

    public async Task PublishFileArrivedAsync(string filePath, CancellationToken ct = default)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMqOptions.AmqpUri) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            exchange: _proxyMockOptions.ProxyExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        var payload = new { filePath, timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var body = Encoding.UTF8.GetBytes(json);

        await channel.BasicPublishAsync(
            exchange: _proxyMockOptions.ProxyExchange,
            routingKey: _proxyMockOptions.RoutingKey,
            body: body,
            cancellationToken: ct);

        _logger.LogInformation("Published file.arrived event for {FilePath}", filePath);
    }
}
