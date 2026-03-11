using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetworkB.Activities.Reporting.Interfaces;
using RabbitMQ.Client;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Options;

namespace NetworkB.Activities.Reporting.Services;

public class RabbitMqAnswerDispatcher : IAnswerDispatcher
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqAnswerDispatcher> _logger;

    public RabbitMqAnswerDispatcher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqAnswerDispatcher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task DispatchAsync(StatusCallbackPayload payload, CancellationToken ct = default)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_options.AmqpUri) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.QueueDeclareAsync(
            "netA.callbacks.queue",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: ct);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var body = Encoding.UTF8.GetBytes(json);

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: "netA.callbacks.queue",
            body: body,
            cancellationToken: ct);

        _logger.LogInformation("Dispatched status callback via RabbitMQ for job {OrigJobId}", payload.OrigJobId);
    }
}
