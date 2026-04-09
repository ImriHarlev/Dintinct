using Proxy.MockService.Options;

namespace Proxy.MockService.Services;

public class FileTransferService
{
    private readonly RabbitMqProxyPublisher _publisher;
    private readonly ILogger<FileTransferService> _logger;
    private readonly Random _random = new();

    public FileTransferService(
        RabbitMqProxyPublisher publisher,
        ILogger<FileTransferService> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public async Task TransferFileAsync(string sourceFilePath, InboxConfig inbox, CancellationToken ct)
    {
        var fileName = Path.GetFileName(sourceFilePath);
        Directory.CreateDirectory(inbox.OutboxPath);

        // 1. Check for unsupported file extension before applying latency
        var extension = Path.GetExtension(sourceFilePath);
        if (inbox.Simulation.UnsupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            var unsupportedFilePath = Path.Combine(inbox.OutboxPath, $"{fileName}.UNSUPPORTED.txt");
            await File.WriteAllTextAsync(unsupportedFilePath, $"Unsupported file type: {extension}", ct);
            _logger.LogWarning("Simulated unsupported file for {FileName} → {UnsupportedFile}", fileName, unsupportedFilePath);
            await _publisher.PublishFileArrivedAsync(unsupportedFilePath, ct);
            return;
        }

        // 2. Simulate network latency
        var latencyMs = GetLatencyMs(inbox.Latency);
        if (latencyMs > 0)
        {
            _logger.LogDebug("Simulating {LatencyMs}ms network latency for {FileName}", latencyMs, fileName);
            await Task.Delay(latencyMs, ct);
        }

        // 3. Check for simulated transfer error
        if (ShouldSimulateError(inbox.Simulation))
        {
            var errorFilePath = Path.Combine(inbox.OutboxPath, $"{fileName}.ERROR.txt");
            await File.WriteAllTextAsync(errorFilePath, $"Simulated transfer error for file: {fileName}", ct);
            _logger.LogWarning("Simulated transfer error for {FileName} → {ErrorFile}", fileName, errorFilePath);
            await _publisher.PublishFileArrivedAsync(errorFilePath, ct);
            return;
        }

        // 4. Copy the actual file to the outbox
        var destinationFilePath = Path.Combine(inbox.OutboxPath, fileName);
        File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
        _logger.LogInformation("Transferred {FileName} → {DestinationPath}", fileName, destinationFilePath);
        await _publisher.PublishFileArrivedAsync(destinationFilePath, ct);
    }

    private int GetLatencyMs(LatencyOptions latency)
    {
        var min = latency.MinMs;
        var max = latency.MaxMs;
        if (min <= 0 && max <= 0) return 0;
        return _random.Next(Math.Max(0, min), Math.Max(min + 1, max + 1));
    }

    private bool ShouldSimulateError(SimulationOptions simulation)
    {
        var rate = simulation.ErrorRatePercent;
        if (rate <= 0) return false;
        if (rate >= 100) return true;
        return _random.Next(100) < rate;
    }
}
