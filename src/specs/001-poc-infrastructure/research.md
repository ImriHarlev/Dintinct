# Research: POC Infrastructure Setup

**Phase**: 0 — Outline & Research  
**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

---

## 1. Temporalio .NET SDK 1.x — Multi-Worker Microservice Architecture

### Decision
Use `Temporalio.Extensions.Hosting` with `AddHostedTemporalWorker()` per microservice, each on its own Task Queue.

### Rationale
- `AddHostedTemporalWorker(taskQueue: "...")` integrates cleanly with `IHostApplicationBuilder` — no custom `BackgroundService` boilerplate needed per worker.
- Activities are registered with `AddScopedActivities<T>()` (new instance per invocation) or `AddSingletonActivities<T>()`.
- A shared `AddTemporalClient()` singleton is registered once in `Shared.Infrastructure` so all workers reuse one gRPC connection.
- Workflows are registered separately from activities (`AddWorkflow<T>()`) — workflow and activity workers can be in completely different processes.

### Key API patterns

```csharp
// Shared.Infrastructure: one-time client registration
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = "localhost:7233";
    opts.Namespace = "default";
});

// Activity-only microservice
builder.Services
    .AddHostedTemporalWorker(taskQueue: "heavy-processing-tasks")
    .AddScopedActivities<DecomposeSplitActivities>();

// Workflow-only microservice (cross-queue activity calls)
await Workflow.ExecuteActivityAsync(
    (DecomposeSplitActivities a) => a.SplitAsync(input),
    new ActivityOptions { TaskQueue = "heavy-processing-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(30) });
```

### Alternatives considered
- Manual `BackgroundService` wrapping `TemporalWorker` — rejected; more boilerplate with no added benefit for this POC.
- Single monolithic worker process for all tasks — rejected; violates CPU/IO separation principle defined in the architecture document.

---

## 2. Temporal Signals and WaitConditionAsync

### Decision
Use `[WorkflowSignal]` attribute on `async Task` methods + `Workflow.WaitConditionAsync(() => predicate)` for stateful aggregation.

### Rationale
- `WaitConditionAsync` suspends the workflow deterministically (no `Task.Delay` or threading primitives).
- Assembly Workflow waits for Manifest signal first, then waits for `receivedChunkCount >= expectedChunkCount`.
- Signal handlers must return `Task` even if synchronous (idiomatic pattern).

### Key API patterns

```csharp
[Workflow]
public class AssemblyWorkflow
{
    private int _receivedChunks;
    private int _expectedChunks;
    private bool _manifestReceived;

    [WorkflowSignal]
    public async Task ManifestArrivedAsync(string manifestPath) { _manifestReceived = true; /* parse ... */ }

    [WorkflowSignal]
    public async Task ChunkArrivedAsync(string chunkPath) => _receivedChunks++;

    [WorkflowRun]
    public async Task<string> RunAsync(string jobId)
    {
        await Workflow.WaitConditionAsync(() => _manifestReceived);
        await Workflow.WaitConditionAsync(() => _receivedChunks >= _expectedChunks);
        // trigger assembly activities ...
    }
}
```

### Alternatives considered
- `Workflow.AwaitAsync` (pre-1.x name for `WaitConditionAsync`) — same concept, confirmed current name is `WaitConditionAsync`.

---

## 3. SignalWithStart from RabbitMQ Consumer

### Decision
Use `WorkflowOptions.SignalWithStart(...)` combined with `StartWorkflowAsync(...)` for the Proxy Event Listener to atomically start or signal the Assembly Workflow.

### Rationale
- Handles the race condition where the manifest arrives before the workflow is started — one atomic call either starts the workflow and delivers the signal, or finds the existing workflow and delivers the signal.
- `IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting` ensures an already-running workflow is reused.

### Key API patterns

```csharp
var options = new WorkflowOptions(id: $"assembly-{jobId}", taskQueue: "assembly-workflow-queue")
{
    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
};
options.SignalWithStart((AssemblyWorkflow wf) => wf.ChunkArrivedAsync(filePath));
await client.StartWorkflowAsync((AssemblyWorkflow wf) => wf.RunAsync(jobId), options);
```

---

## 4. Serilog Structured Logging in .NET 10

### Decision
Use `Serilog.AspNetCore` (for Web API projects) and `Serilog.Extensions.Hosting` (for Worker Service projects) with `RenderedCompactJsonFormatter` to console.

### Rationale
- `UseSerilog()` replaces the default Microsoft logging with a single configuration point.
- `Enrich.WithProperty("Service", "<name>")` adds the service name to every log line — critical for log aggregation without grep.
- All connection success/failure lines use `LogInformation`/`LogError` with structured message templates per FR-021.

### NuGet packages (per project type)

| Package | Web API | Worker Service |
|---------|---------|----------------|
| `Serilog.AspNetCore` | ✅ | ❌ |
| `Serilog.Extensions.Hosting` | ❌ | ✅ |
| `Serilog.Sinks.Console` | ✅ | ✅ |
| `Serilog.Formatting.Compact` | ✅ | ✅ |
| `Serilog.Enrichers.Environment` | ✅ | ✅ |

### Standard startup log format (FR-021)

```csharp
_logger.LogInformation("{Service}: {Dependency} connected successfully", serviceName, depName);
// e.g., "NetworkA.Ingestion.API: MongoDB connected successfully"
// e.g., "NetworkA.Activities.JobSetup: Redis connected successfully"
// e.g., "NetworkA.Decomposition.Workflow: Temporal worker registered on queue setup-tasks"
```

---

## 5. MongoDB.Driver 3.x Repository Pattern

### Decision
Use `IMongoClient` singleton + `IMongoDatabase` singleton registered in `Shared.Infrastructure`; typed repository interfaces in `Shared.Contracts`; implementations in the consuming service project.

### Rationale
- `Shared.Infrastructure` must not leak into `Shared.Contracts` (Constitution Principle VI), so interfaces live in `Shared.Contracts` and implementations reference `Shared.Infrastructure`.
- MongoDB.Driver 3.x dropped LINQ2 — LINQ3 (standard `IQueryable`) is the only provider.
- `MongoClient` is now `IDisposable` — always register as singleton to avoid connection pool churn.

### Breaking changes in 3.x
- LINQ2 removed — use filter builders (`Builders<T>.Filter`) or LINQ3 expressions.
- `LinqProvider.V2` option no longer exists.
- `IMongoQueryable` is `IQueryable`.

### DI registration pattern (Shared.Infrastructure)

```csharp
services.AddSingleton<IMongoClient>(sp =>
{
    var connStr = sp.GetRequiredService<IOptions<MongoDbOptions>>().Value.ConnectionString;
    return new MongoClient(MongoClientSettings.FromConnectionString(connStr));
});
services.AddSingleton<IMongoDatabase>(sp =>
{
    var dbName = sp.GetRequiredService<IOptions<MongoDbOptions>>().Value.DatabaseName;
    return sp.GetRequiredService<IMongoClient>().GetDatabase(dbName);
});
```

---

## 6. RabbitMQ.Client 7.x Async Consumer

### Decision
Use `AsyncEventingBasicConsumer` with `DispatchConsumersAsync = true` on `ConnectionFactory`, wrapped in a `BackgroundService`.

### Breaking changes in 7.x
- All I/O is now fully async (`CreateConnectionAsync`, `QueueDeclareAsync`, `BasicConsumeAsync`).
- `IConnection` and `IChannel` now implement `IAsyncDisposable`.
- Body (`ea.Body`) is `ReadOnlyMemory<byte>` — **must call `.ToArray()` before any `await`** to avoid reading a recycled buffer.
- Never call `CloseAsync` from inside a consumer handler — causes deadlock.

### Critical pattern

```csharp
private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
{
    var body = ea.Body.ToArray(); // copy BEFORE any await
    try
    {
        var msg = JsonSerializer.Deserialize<ProxyFileEvent>(body)!;
        await RouteEventAsync(msg);
        await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed processing delivery {Tag}", ea.DeliveryTag);
        await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
    }
}
```

---

## 7. StackExchange.Redis 2.x Read-Through Cache

### Decision
Register `IConnectionMultiplexer` as singleton in `Shared.Infrastructure`; call `GetDatabase()` per operation (it is a cheap pass-through).

### Key rules
- `IConnectionMultiplexer` = singleton always.
- Never inject `IDatabase` as a singleton — `GetDatabase()` is a free wrapper, not a real connection.
- Use versioned key prefix (`v1:proxyconfig:{formatKey}`) for future cache invalidation.
- `AbortOnConnectFail = false` — prevents crash on slow startup; retries automatically.

### ProxyConfiguration cache pattern

```csharp
var db = _redis.GetDatabase();
var cached = await db.StringGetAsync($"v1:proxyconfig:{format}");
if (cached.HasValue) return JsonSerializer.Deserialize<ProxyConfiguration>(cached!);
var config = await _mongoRepo.FindByFormatAsync(format);
if (config is not null)
    await db.StringSetAsync($"v1:proxyconfig:{format}", JsonSerializer.Serialize(config), TimeSpan.FromMinutes(30));
return config;
```

---

## 8. Template Boilerplate Cleanup (FR-001)

### Decision
Before any POC code, all template boilerplate must be removed. This is the **first implementation step**.

### Items to remove per project type

| Project | Items to Remove |
|---------|----------------|
| `webapi` template projects (Ingestion API, Callback Receiver) | `WeatherForecast` record, `app.MapGet("/weatherforecast", ...)` minimal API route, `builder.Services.AddOpenApi()`, `Microsoft.AspNetCore.OpenApi` package reference, `app.UseHttpsRedirection()` (POC is HTTP-only) |
| `worker` template projects (all Activity and Workflow workers) | Template `Worker.cs` (the placeholder `BackgroundService` with a 1-second loop) |
| All projects | Default `appsettings.json` content — replace with project-specific configuration skeleton |
| All projects | Any auto-generated `Class1.cs` files in class library projects |

### Post-cleanup state per project
- `Program.cs` — minimal host setup only; no sample routes or placeholder services.
- `appsettings.json` — project-specific keys only; no Microsoft/ASP.NET defaults retained.
- `Worker.cs` — deleted; replaced with the actual Temporal worker registration in `Program.cs`.
