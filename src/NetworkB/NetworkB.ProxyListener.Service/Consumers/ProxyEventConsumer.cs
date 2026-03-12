using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetworkB.ProxyListener.Service.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Contracts.Signals;
using Shared.Infrastructure.Options;
using Temporalio.Client;

namespace NetworkB.ProxyListener.Service.Consumers;

public class ProxyEventConsumer : IHostedService, IAsyncDisposable
{
    private readonly ITemporalClient _temporalClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyListenerOptions _listenerOptions;
    private readonly RabbitMqOptions _rabbitMqOptions;
    private readonly NetworkACallbackOptions _callbackOptions;
    private readonly AssemblyOptions _assemblyOptions;
    private readonly ILogger<ProxyEventConsumer> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public ProxyEventConsumer(
        ITemporalClient temporalClient,
        IHttpClientFactory httpClientFactory,
        IOptions<ProxyListenerOptions> listenerOptions,
        IOptions<RabbitMqOptions> rabbitMqOptions,
        IOptions<NetworkACallbackOptions> callbackOptions,
        IOptions<AssemblyOptions> assemblyOptions,
        ILogger<ProxyEventConsumer> logger)
    {
        _temporalClient = temporalClient;
        _httpClientFactory = httpClientFactory;
        _listenerOptions = listenerOptions.Value;
        _rabbitMqOptions = rabbitMqOptions.Value;
        _callbackOptions = callbackOptions.Value;
        _assemblyOptions = assemblyOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMqOptions.AmqpUri) };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await _channel.ExchangeDeclareAsync(
            _listenerOptions.ProxyExchange,
            ExchangeType.Direct,
            durable: true,
            cancellationToken: cancellationToken);

        await _channel.QueueDeclareAsync(
            _listenerOptions.ProxyQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(
            _listenerOptions.ProxyQueue,
            _listenerOptions.ProxyExchange,
            routingKey: "file.arrived",
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += HandleMessageAsync;

        await _channel.BasicConsumeAsync(
            _listenerOptions.ProxyQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        _logger.LogInformation("NetworkB.ProxyListener.Service: RabbitMQ connected successfully");
        _logger.LogInformation("NetworkB.ProxyListener.Service: Temporal worker registered on queue assembly-workflow");
    }

    private async Task HandleMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        var body = ea.Body.ToArray();

        try
        {
            var json = Encoding.UTF8.GetString(body);
            using var doc = JsonDocument.Parse(json);
            var filePath = doc.RootElement.GetProperty("filePath").GetString() ?? string.Empty;
            var fileName = Path.GetFileName(filePath);

            var jobId = ExtractJobId(fileName);
            var assemblyWorkflowId = $"assembly-{jobId}";
            var workflowOptions = new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
            {
                StartSignal = null
            };

            if (fileName.EndsWith(".ERROR.txt", StringComparison.OrdinalIgnoreCase))
            {
                var chunkName = fileName[..^".ERROR.txt".Length];

                // If a HARDFAIL marker itself failed to transfer, the proxy would retry it and
                // eventually cascade: HARDFAIL → HARDFAIL.ERROR → retry → HARDFAIL.HARDFAIL → ...
                // Break the cycle by treating any HARDFAIL transfer failure as a direct hard-fail
                // signal for the original chunk, without going through Network A's retry machinery.
                if (chunkName.EndsWith(".HARDFAIL.txt", StringComparison.OrdinalIgnoreCase))
                {
                    var originalChunkName = chunkName[..^".HARDFAIL.txt".Length];
                    _logger.LogWarning(
                        "HARDFAIL marker transfer failed for {ChunkName}, signalling hard-fail for original chunk {OriginalChunkName}",
                        chunkName, originalChunkName);
                    var hardFailSignal = new HardFailSignal(originalChunkName);
                    await _temporalClient.StartWorkflowAsync(
                        "AssemblyWorkflow",
                        [_assemblyOptions.TimeoutMinutes],
                        new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
                        {
                            StartSignal = "HardFail",
                            StartSignalArgs = [hardFailSignal]
                        });
                }
                else
                {
                    _logger.LogInformation("Proxy error for chunk {ChunkName}, sending retry to Network A", chunkName);
                    var httpClient = _httpClientFactory.CreateClient("NetworkA");
                    await httpClient.PostAsJsonAsync(
                        $"{_callbackOptions.CallbackBaseUrl}/api/v1/callbacks/retry",
                        new { origJobId = jobId, chunkName });
                }
            }
            else if (fileName.EndsWith(".UNSUPPORTED.txt", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Unsupported file signal for {FileName}", fileName);
                var signal = new UnsupportedFileSignal(filePath);
                await _temporalClient.StartWorkflowAsync(
                    "AssemblyWorkflow",
                    [_assemblyOptions.TimeoutMinutes],
                    new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
                    {
                        StartSignal = "UnsupportedFile",
                        StartSignalArgs = [signal]
                    });
            }
            else if (fileName.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Manifest arrived for job {JobId}", jobId);
                var signal = new ManifestSignal(filePath);
                await _temporalClient.StartWorkflowAsync(
                    "AssemblyWorkflow",
                    [_assemblyOptions.TimeoutMinutes],
                    new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
                    {
                        StartSignal = "ManifestArrived",
                        StartSignalArgs = [signal]
                    });
            }
            else if (fileName.EndsWith(".HARDFAIL.txt", StringComparison.OrdinalIgnoreCase))
            {
                var chunkName = fileName[..^".HARDFAIL.txt".Length];
                _logger.LogInformation("Hard fail for chunk {ChunkName}", chunkName);
                var signal = new HardFailSignal(chunkName);
                await _temporalClient.StartWorkflowAsync(
                    "AssemblyWorkflow",
                    [_assemblyOptions.TimeoutMinutes],
                    new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
                    {
                        StartSignal = "HardFail",
                        StartSignalArgs = [signal]
                    });
            }
            else
            {
                // Data chunk
                _logger.LogInformation("Chunk arrived: {FileName}", fileName);
                var signal = new ChunkSignal(filePath);
                await _temporalClient.StartWorkflowAsync(
                    "AssemblyWorkflow",
                    [_assemblyOptions.TimeoutMinutes],
                    new WorkflowOptions(assemblyWorkflowId, "assembly-workflow")
                    {
                        StartSignal = "ChunkArrived",
                        StartSignalArgs = [signal]
                    });
            }

            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing proxy event");
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private static string ExtractJobId(string fileName)
    {
        var underscoreIndex = fileName.IndexOf('_');
        return underscoreIndex > 0 ? fileName[..underscoreIndex] : fileName;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
            await _channel.CloseAsync(cancellationToken);
        if (_connection is not null)
            await _connection.CloseAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
