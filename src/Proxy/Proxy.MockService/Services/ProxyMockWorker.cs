using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Proxy.MockService.Options;

namespace Proxy.MockService.Services;

public class ProxyMockWorker : BackgroundService
{
    private readonly ProxyMockOptions _options;
    private readonly FileTransferService _transferService;
    private readonly ILogger<ProxyMockWorker> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ProxyMockWorker(
        IOptions<ProxyMockOptions> options,
        FileTransferService transferService,
        ILogger<ProxyMockWorker> logger)
    {
        _options = options.Value;
        _transferService = transferService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Inboxes.Count == 0)
        {
            _logger.LogWarning("No inboxes configured. ProxyMockWorker is idle.");
            return Task.CompletedTask;
        }

        foreach (var inbox in _options.Inboxes)
        {
            StartWatcher(inbox, stoppingToken);
        }

        _logger.LogInformation("ProxyMockWorker started. Watching {Count} inbox(es).", _options.Inboxes.Count);

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void StartWatcher(InboxConfig inbox, CancellationToken stoppingToken)
    {
        if (!Directory.Exists(inbox.InboxPath))
        {
            Directory.CreateDirectory(inbox.InboxPath);
            _logger.LogInformation("Created inbox directory: {Path}", inbox.InboxPath);
        }

        var watcher = new FileSystemWatcher(inbox.InboxPath)
        {
            Filter = inbox.FilePattern,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };

        watcher.Created += (_, e) => OnFileDetected(e.FullPath, inbox, stoppingToken);
        watcher.Changed += (_, e) => OnFileDetected(e.FullPath, inbox, stoppingToken);

        _watchers.Add(watcher);
        _logger.LogInformation("Watching inbox {InboxPath} → outbox {OutboxPath} (pattern: {Pattern})",
            inbox.InboxPath, inbox.OutboxPath, inbox.FilePattern);
    }

    private void OnFileDetected(string filePath, InboxConfig inbox, CancellationToken stoppingToken)
    {
        if (!_inFlight.TryAdd(filePath, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Brief wait to ensure the file is fully written before reading
                await Task.Delay(200, stoppingToken);

                _logger.LogInformation("File detected: {FilePath}", filePath);
                await _transferService.TransferFileAsync(filePath, inbox, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to transfer file {FilePath}", filePath);
            }
            finally
            {
                _inFlight.TryRemove(filePath, out _);
            }
        }, stoppingToken);
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        base.Dispose();
    }
}
