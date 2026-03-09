namespace Shared.Infrastructure.Options;

public class RabbitMqOptions
{
    public string AmqpUri { get; set; } = string.Empty;
    public string IngestionExchange { get; set; } = string.Empty;
    public string IngestionQueue { get; set; } = string.Empty;
}
