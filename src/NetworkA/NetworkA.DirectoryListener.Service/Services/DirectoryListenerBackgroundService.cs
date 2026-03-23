using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NetworkA.DirectoryListener.Service.Options;
using ZiggyCreatures.Caching.Fusion;

namespace NetworkA.DirectoryListener.Service.Services;

public class DirectoryListenerBackgroundService : BackgroundService
{
    private readonly DirectoryListenerOptions _options;
    private readonly IInputSubmissionService _inputSubmissionService;
    private readonly ILogger<DirectoryListenerBackgroundService> _logger;
    private readonly IFusionCache _cache;
    private readonly Channel<QueuedFile> _queue;
    private readonly ConcurrentDictionary<string, byte> _queuedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _stableFor;
    private readonly int _maxConcurrency;
    private static readonly TimeSpan ProcessedFilesRetention = TimeSpan.FromMinutes(5);

    public DirectoryListenerBackgroundService(
        IOptions<DirectoryListenerOptions> options,
        IInputSubmissionService inputSubmissionService,
        ILogger<DirectoryListenerBackgroundService> logger,
        IFusionCache cache)
    {
        _options = options.Value;
        _inputSubmissionService = inputSubmissionService;
        _logger = logger;
        _cache = cache;
        _pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        _stableFor = TimeSpan.FromSeconds(_options.StableForSeconds);
        _maxConcurrency = Math.Max(1, _options.MaxConcurrency);
        _queue = Channel.CreateBounded<QueuedFile>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        ConfigureWatchers();

        await base.StartAsync(cancellationToken);

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching directory {DirectoryPath} with filter {Filter}", watcher.Path, watcher.Filter);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Directory listener started. Poll={PollSeconds}s Stable={StableSeconds}s Concurrency={Concurrency}",
            _pollInterval.TotalSeconds,
            _stableFor.TotalSeconds,
            _maxConcurrency);

        var consumerTasks = Enumerable.Range(0, _maxConcurrency)
            .Select(_ => ConsumeQueueAsync(stoppingToken))
            .ToArray();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var directory in _options.Directories)
                        EnqueueDirectorySnapshot(directory);

                    await Task.Delay(_pollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Directory polling failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _queue.Writer.TryComplete();

        try
        {
            await Task.WhenAll(consumerTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _logger.LogInformation("Directory listener stopped");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var watcher in _watchers)
            watcher.EnableRaisingEvents = false;

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();

        base.Dispose();
    }

    private void ConfigureWatchers()
    {
        ValidateOptions();

        foreach (var directory in _options.Directories)
        {
            Directory.CreateDirectory(directory.Path);

            var watcher = new FileSystemWatcher(directory.Path, string.IsNullOrWhiteSpace(directory.Filter) ? "*.*" : directory.Filter)
            {
                IncludeSubdirectories = directory.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Created += (_, args) => QueueFile(directory, args.FullPath);
            watcher.Renamed += (_, args) => QueueFile(directory, args.FullPath);
            watcher.Error += (_, args) =>
                _logger.LogError(args.GetException(), "Directory watcher error for listener {ListenerName} on path {DirectoryPath}", directory.Name, directory.Path);

            _watchers.Add(watcher);
        }
    }

    private void EnqueueDirectorySnapshot(WatchedDirectoryOptions directory)
    {
        if (!Directory.Exists(directory.Path))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directory.Path);
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                directory.Path,
                string.IsNullOrWhiteSpace(directory.Filter) ? "*.*" : directory.Filter,
                directory.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate directory {DirectoryPath}", directory.Path);
            return;
        }

        foreach (var filePath in files)
            QueueFile(directory, filePath);
    }

    private void QueueFile(WatchedDirectoryOptions directory, string filePath)
    {
        if (!IsCandidateFile(filePath))
            return;

        var key = BuildFileKey(directory, filePath);
        var processedStamp = _cache.TryGet<FileStamp>(key);
        if (processedStamp.HasValue)
        {
            var currentStamp = GetStamp(filePath);
            if (currentStamp is not null && currentStamp.Value.Equals(processedStamp.Value))
                return;
        }

        if (!_queuedFiles.TryAdd(key, 0))
            return;

        if (_queue.Writer.TryWrite(new QueuedFile(key, filePath, directory)))
        {
            _logger.LogInformation("Queued new file {FilePath} for listener {ListenerName}", filePath, directory.Name);
            return;
        }

        _queuedFiles.TryRemove(key, out _);
        _logger.LogWarning("Failed to queue file {FilePath} for listener {ListenerName}", filePath, directory.Name);
    }

    private async Task ConsumeQueueAsync(CancellationToken ct)
    {
        await foreach (var queuedFile in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await TryProcessFileAsync(queuedFile, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit file {FilePath} for listener {ListenerName}", queuedFile.FilePath, queuedFile.Directory.Name);
            }
            finally
            {
                _queuedFiles.TryRemove(queuedFile.Key, out _);
            }
        }
    }

    private async Task TryProcessFileAsync(QueuedFile queuedFile, CancellationToken ct)
    {
        if (!IsCandidateFile(queuedFile.FilePath))
            return;

        var stableStamp = await WaitForStableAsync(queuedFile.FilePath, ct);
        if (stableStamp is null)
            return;

        var processedStamp = _cache.TryGet<FileStamp>(queuedFile.Key);
        if (processedStamp.HasValue && processedStamp.Value.Equals(stableStamp.Value))
            return;

        var jobId = await _inputSubmissionService.SubmitAsync(queuedFile.Directory, queuedFile.FilePath, ct);
        _cache.Set(queuedFile.Key, stableStamp.Value, ProcessedFilesRetention);

        _logger.LogInformation(
            "Queued file {FilePath} submitted as job {JobId} for listener {ListenerName}",
            queuedFile.FilePath,
            jobId,
            queuedFile.Directory.Name);
    }

    private async Task<FileStamp?> WaitForStableAsync(string filePath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.ReadyCheckMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var firstStamp = GetStamp(filePath);
            if (firstStamp is null)
            {
                if (attempt < _options.ReadyCheckMaxAttempts)
                    await Task.Delay(_options.ReadyCheckDelayMilliseconds, ct);

                continue;
            }

            var fileAge = DateTime.UtcNow - firstStamp.Value.LastWriteUtc;
            var waitTime = fileAge < _stableFor
                ? _stableFor - fileAge + TimeSpan.FromMilliseconds(100)
                : TimeSpan.FromMilliseconds(100);

            await Task.Delay(waitTime, ct);

            var secondStamp = GetStamp(filePath);
            if (secondStamp is not null && secondStamp.Value.Equals(firstStamp.Value))
                return secondStamp.Value;

            if (attempt < _options.ReadyCheckMaxAttempts)
                await Task.Delay(_options.ReadyCheckDelayMilliseconds, ct);
        }

        _logger.LogWarning("File {FilePath} was not stable after {Attempts} attempts", filePath, _options.ReadyCheckMaxAttempts);
        return null;
    }

    private static FileStamp? GetStamp(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return null;

            return new FileStamp(info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFileKey(WatchedDirectoryOptions directory, string filePath)
        => $"{directory.Name}:{Path.GetFullPath(filePath)}";

    private static bool IsCandidateFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Directory.Exists(filePath))
            return false;

        var name = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith(".", StringComparison.Ordinal))
            return false;

        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void ValidateOptions()
    {
        if (_options.PollIntervalSeconds <= 0)
            throw new InvalidOperationException("DirectoryListener:PollIntervalSeconds must be greater than 0.");

        if (_options.StableForSeconds <= 0)
            throw new InvalidOperationException("DirectoryListener:StableForSeconds must be greater than 0.");

        if (_options.MaxConcurrency <= 0)
            throw new InvalidOperationException("DirectoryListener:MaxConcurrency must be greater than 0.");

        if (_options.QueueCapacity <= 0)
            throw new InvalidOperationException("DirectoryListener:QueueCapacity must be greater than 0.");

        if (_options.ReadyCheckMaxAttempts <= 0)
            throw new InvalidOperationException("DirectoryListener:ReadyCheckMaxAttempts must be greater than 0.");

        if (_options.ReadyCheckDelayMilliseconds <= 0)
            throw new InvalidOperationException("DirectoryListener:ReadyCheckDelayMilliseconds must be greater than 0.");

        if (_options.Directories.Count == 0)
            throw new InvalidOperationException("DirectoryListener requires at least one configured directory.");

        foreach (var directory in _options.Directories)
        {
            if (string.IsNullOrWhiteSpace(directory.Name))
                throw new InvalidOperationException("Each configured directory requires a Name.");

            if (string.IsNullOrWhiteSpace(directory.Path))
                throw new InvalidOperationException($"DirectoryListener '{directory.Name}' requires a Path.");

            if (string.IsNullOrWhiteSpace(directory.CallingSystemId))
                throw new InvalidOperationException($"DirectoryListener '{directory.Name}' requires a CallingSystemId.");

            if (string.IsNullOrWhiteSpace(directory.CallingSystemName))
                throw new InvalidOperationException($"DirectoryListener '{directory.Name}' requires a CallingSystemName.");

            if (string.IsNullOrWhiteSpace(directory.TargetPath))
                throw new InvalidOperationException($"DirectoryListener '{directory.Name}' requires a TargetPath.");

            if (string.IsNullOrWhiteSpace(directory.TargetNetwork))
                throw new InvalidOperationException($"DirectoryListener '{directory.Name}' requires a TargetNetwork.");
        }
    }

    private sealed record QueuedFile(string Key, string FilePath, WatchedDirectoryOptions Directory);
    private readonly record struct FileStamp(long Size, DateTime LastWriteUtc);
}
