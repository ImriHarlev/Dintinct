# Interface Contracts (Shared.Contracts)

**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

All interfaces below live in `Shared.Contracts`. No implementation detail may be placed in this project (Constitution Principle VI). Every concrete dependency injected across services must use one of these interface types as the service key (FR-020).

`[Activity]` attributes are **not** placed on interface methods — they belong on the concrete activity class methods only. `Shared.Contracts` must not reference the `Temporalio` NuGet package.

---

## Shared Value Types (Shared.Contracts)

These types are used across multiple services and both networks. They are standalone records — not nested inside any activity or workflow type.

### `FileDescriptor`

```csharp
public record FileDescriptor(
    string OriginalRelativePath,   // e.g. "docs/report.pdf" — the output path after reverse conversion
    string OriginalFormat,         // file extension before any conversion, e.g. "pdf", "jpeg"
    string AppliedConversion,      // "DirectToProxy" | "PNG" | "DOCX_AND_PNG" | "TXT"
    IReadOnlyList<ConvertedFileDescriptor> ConvertedFiles
);
```

### `ConvertedFileDescriptor`

```csharp
public record ConvertedFileDescriptor(
    string ConvertedRelativePath,  // equals OriginalRelativePath for "DirectToProxy"
    IReadOnlyList<ChunkDescriptor> Chunks
);
```

### `ChunkDescriptor`

```csharp
public record ChunkDescriptor(
    string Name,       // physical filename, e.g. "3fa85f64-..._chunk_1.bin"
    int Index,         // 1-based concatenation order within this ConvertedFile
    string Checksum    // "N/A" in the POC mock
);
```

### `FileResult`

```csharp
public record FileResult(
    FileTransferStatus Status,
    string DirPath    // matches OriginalRelativePath — used as dir_path in the CSV report
);
```

---

## Repository Interfaces

### `IJobRepository` (Network A)

```csharp
public interface IJobRepository
{
    Task<Job?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<Job?> FindByExternalIdAsync(string externalId, CancellationToken ct = default);
    Task UpsertAsync(Job job, CancellationToken ct = default);
    Task IncrementChunkRetryCountAsync(string jobId, string chunkName, CancellationToken ct = default);
}
```

### `IProxyConfigRepository` (Network A)

```csharp
public interface IProxyConfigRepository
{
    Task<ProxyConfiguration?> FindBySourceFormatAsync(string sourceFormat, CancellationToken ct = default);
    Task<IReadOnlyList<ProxyConfiguration>> GetAllAsync(CancellationToken ct = default);
}
```

### `IAssemblyBlueprintRepository` (Network B)

Maps to `AssemblyBlueprint` in Network B's MongoDB.

```csharp
public interface IAssemblyBlueprintRepository
{
    Task<AssemblyBlueprint?> FindByJobIdAsync(string jobId, CancellationToken ct = default);
    Task UpsertAsync(AssemblyBlueprint blueprint, CancellationToken ct = default);
}
```

---

## Cache Interface

### `IProxyConfigCache` (Network A)

Abstracts the Redis read-through cache for proxy configuration rules.

```csharp
public interface IProxyConfigCache
{
    Task<ProxyConfiguration?> GetAsync(string sourceFormat, CancellationToken ct = default);
    Task InvalidateAsync(string sourceFormat, CancellationToken ct = default);
}
```

---

## Service Interfaces

### `IIngestionService` (Network A — Ingestion API)

```csharp
public interface IIngestionService
{
    Task<string> StartJobAsync(IngestionRequestPayload request, CancellationToken ct = default);
}
```

Returns the new internal `JobId`.

### `ICallbackService` (Network A — Callback Receiver)

```csharp
public interface ICallbackService
{
    Task HandleFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default);
    Task HandleChunkRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default);
}
```

### `INetworkAClient` (Network B — outbound HTTP to Network A)

```csharp
public interface INetworkAClient
{
    Task SendFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default);
    Task SendRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default);
}
```

### `IAnswerDispatcher` (Network B — Reporting & Dispatch Activity)

Abstracts the delivery of `StatusCallbackPayload` via RabbitMQ or FileSystem based on `answerType`.

```csharp
public interface IAnswerDispatcher
{
    Task DispatchAsync(StatusCallbackPayload payload, CancellationToken ct = default);
}
```

### `ICsvReportWriter` (Network B — Reporting & Dispatch Activity)

```csharp
public interface ICsvReportWriter
{
    Task WriteAsync(string outputPath, IReadOnlyList<FileResult> results, CancellationToken ct = default);
}
```

---

## Activity Interfaces

These interfaces define the activity contracts. The `[Activity]` attribute is applied on the **concrete class methods only**, not here. `Shared.Contracts` must remain free of Temporalio NuGet references.

### `IJobSetupActivities` (Task Queue: `setup-tasks`)

```csharp
public interface IJobSetupActivities
{
    Task<WorkflowConfiguration> FetchConfigurationAsync(string jobId);
}
```

Where `WorkflowConfiguration`:

```csharp
public record WorkflowConfiguration(
    string JobId,
    string SourcePath,
    string TargetPath,
    int MockChunkCount,   // number of mock chunks to generate per converted file (FR-008)
    int MaxRetryCount,
    IReadOnlyList<ProxyConfiguration> ProxyRules
);
```

### `IHeavyProcessingActivities` (Task Queue: `heavy-processing-tasks`)

```csharp
public interface IHeavyProcessingActivities
{
    Task<DecompositionMetadata> DecomposeAndSplitAsync(WorkflowConfiguration config);
}
```

Where `DecompositionMetadata` — mirrors the manifest hierarchy so `ManifestActivity` can serialize it directly:

```csharp
public record DecompositionMetadata(
    string JobId,
    string PackageType,            // "zip", "rar", "7z", "gz", "directory", "file"
    string OriginalPackageName,
    string TargetPath,
    int TotalChunks,               // sum of all chunks across all files and converted files
    IReadOnlyList<FileDescriptor> Files
);
```

### `IManifestActivities` (Task Queue: `manifest-tasks`)

```csharp
public interface IManifestActivities
{
    Task WriteManifestAsync(DecompositionMetadata metadata);
}
```

### `IDispatchActivities` (Task Queue: `retry-dispatch-tasks`)

```csharp
public interface IDispatchActivities
{
    Task RetryChunkAsync(string jobId, string chunkName);
    Task WriteHardFailAsync(string jobId, string chunkName);
}
```

### `IManifestStateActivities` (Task Queue: `manifest-assembly-tasks`, Network B)

```csharp
public interface IManifestStateActivities
{
    Task<AssemblyBlueprint> ParseAndPersistManifestAsync(string manifestFilePath);
}
```

### `IHeavyAssemblyActivities` (Task Queue: `heavy-assembly-tasks`, Network B)

The activity receives the full blueprint (for the reversal plan) and the list of chunk file paths
that actually arrived on disk. The paths are passed from workflow memory because MongoDB only
persists the blueprint structure — the runtime-collected paths live in workflow state.

```csharp
public interface IHeavyAssemblyActivities
{
    Task<IReadOnlyList<FileResult>> AssembleAndValidateAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<string> receivedChunkPaths);
}
```

### `IReportingActivities` (Task Queue: `callback-dispatch-tasks`, Network B)

```csharp
public interface IReportingActivities
{
    Task GenerateAndDispatchReportAsync(
        AssemblyBlueprint blueprint,
        IReadOnlyList<FileResult> fileResults,
        JobStatus finalStatus);
}
```
