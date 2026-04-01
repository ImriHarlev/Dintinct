---
name: Workflow Activity Simplification
overview: After removing MongoDB and Redis, several activities are now misleadingly named, have dead parameters, or are pure no-ops. This plan removes what no longer has purpose, renames what is misleading, and collapses one entire worker project that only existed for config loading.
todos:
  - id: remove-update-blueprint-status
    content: Delete UpdateBlueprintStatusActivities.cs and remove its call from AssemblyWorkflow and Program.cs
    status: completed
  - id: rename-parse-manifest
    content: Rename ParseAndPersistManifest → ParseManifest across class, method, and activity string in AssemblyWorkflow
    status: completed
  - id: fetch-config-local-activity
    content: Delete FetchConfiguration activity entirely; inject IOptions<ProxyConfigOptions> and IOptions<RetryPolicyOptions> into DecompositionWorkflow constructor and build WorkflowConfiguration inline. Delete NetworkA.Activities.JobSetup project and setup-tasks queue.
    status: completed
  - id: remove-activity-config-layer
    content: Delete WorkflowActivityConfigLocalActivity, WorkflowActivityConfig, ActivityTimeoutConfig, RetryPolicyConfig. Inject IOptions<WorkflowActivityConfigOptions> directly into both workflows. Update ActivityOptionsExtensions to extend ActivityTimeoutEntry instead.
    status: completed
  - id: remove-jobid-param
    content: Remove jobId parameter from RetryChunkAsync and WriteHardFailAsync; update call sites in DecompositionWorkflow
    status: completed
  - id: extract-jobid-property
    content: Extract private JobId property in DecompositionWorkflow to eliminate duplicate WorkflowId.Replace calls
    status: completed
  - id: remove-blueprint-timestamps
    content: Remove CreatedAt and UpdatedAt from AssemblyBlueprint and their initializers in ParseManifestActivities
    status: completed
  - id: slim-decompose-return
    content: Replace the 6 blank fields in DecomposeAndSplit's returned DecompositionMetadata with a lean SplitResult type. Build full DecompositionMetadata inline in the workflow from SplitResult + _config + request before passing to WriteManifest.
    status: completed
  - id: slim-prepare-source-signature
    content: Change PrepareSourceAsync(WorkflowConfiguration) to PrepareSourceAsync(string jobId, string sourcePath); update the call site in DecompositionWorkflow.
    status: completed
  - id: derive-assembly-dir
    content: Remove assemblyDir parameter from RepackAndFinalizeAsync; derive it inside the activity as Path.Combine(blueprint.TargetPath, $"_assembly_{blueprint.Id}"). Update AssemblyWorkflow call site.
    status: completed
isProject: false
---

# Workflow & Activity Post-Mongo/Redis Simplification

## What Changed and Why It Matters

With MongoDB and Redis gone, several problems emerged in the current code:

1. `**UpdateBlueprintStatus**` is an activity that does nothing but log one line — no DB write, no side effect. It adds a Temporal activity overhead for zero value.
2. `**ParseAndPersistManifest**` still advertises "persist" in its name, but the persist call was deleted. The name is now a lie.
3. `**FetchConfiguration**` routes to an entire separate worker process (`NetworkA.Activities.JobSetup` on `setup-tasks` queue) to read two `IOptions<>` objects — pure in-memory config that was only externalised because the old version queried MongoDB/Redis.
4. `**WorkflowActivityConfigLocalActivity**` + `**WorkflowActivityConfig**` + `**ActivityTimeoutConfig**` + `**RetryPolicyConfig**` is a redundant intermediate layer. `appsettings.json` → `WorkflowActivityConfigOptions` already holds the same data in the same shape. The mapping step existed for MongoDB deserialization. Now it's just `IOptions<>` → duplicate model → `ActivityOptions`.
5. `**RetryChunk` and `WriteHardFail**` both accept a `jobId` string they only use for logging; the actual file-path resolution ignores it. Same for `DecompositionWorkflow` which derives `jobId` twice from `WorkflowId`.
6. `**AssemblyBlueprint.CreatedAt/UpdatedAt**` are MongoDB timestamp artifacts with no readers.

---

## Changes

### 1. Remove `UpdateBlueprintStatus` activity

**File to delete:** `[src/NetworkB/Activities/NetworkB.Activities.ManifestState/Activities/UpdateBlueprintStatusActivities.cs](src/NetworkB/Activities/NetworkB.Activities.ManifestState/Activities/UpdateBlueprintStatusActivities.cs)`

**File to update:** `[src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs](src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs)` — remove the `ExecuteActivityAsync("UpdateBlueprintStatus", ...)` call. The `blueprint.Status = finalStatus.ToString()` line already sets the status in-workflow, which is sufficient.

Also remove registration of `UpdateBlueprintStatusActivities` from the assembly worker's `Program.cs`.

---

### 2. Rename `ParseAndPersistManifest` → `ParseManifest`

**Files to update:**

- `[src/NetworkB/Activities/NetworkB.Activities.ManifestState/Activities/ParseAndPersistManifestActivities.cs](src/NetworkB/Activities/NetworkB.Activities.ManifestState/Activities/ParseAndPersistManifestActivities.cs)` — rename class to `ParseManifestActivities`, rename method to `ParseManifestAsync`
- `[src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs](src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs)` — update activity string from `"ParseAndPersistManifest"` → `"ParseManifest"`
- Assembly worker `Program.cs` — update activity registration

---

### 3. Delete `FetchConfiguration` entirely, delete `NetworkA.Activities.JobSetup`

`JobSetupActivities.FetchConfigurationAsync` has no business being an activity — or even a local activity. Every value it assembles is already in the workflow:

- `jobId` → derived from `WorkflowId` (available inline)
- `sourcePath` / `targetPath` → `request.SourcePath` / `request.TargetPath` (workflow input)
- `ProxyRules` → `IOptions<ProxyConfigOptions>` (inject into workflow constructor)
- `MaxRetryCount` → `IOptions<RetryPolicyOptions>` (inject into workflow constructor)

**Update** `[src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs](src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs)`:

```csharp
public class DecompositionWorkflow(
    IOptions<ProxyConfigOptions> proxyConfig,
    IOptions<RetryPolicyOptions> retryOptions)
{
    [WorkflowRun]
    public async Task RunAsync(IngestionRequestPayload request)
    {
        _activityConfig = await TemporalWorkflow.ExecuteLocalActivityAsync(...); // unchanged

        _config = new WorkflowConfiguration(
            JobId: JobId,
            SourcePath: request.SourcePath,
            TargetPath: request.TargetPath,
            MaxRetryCount: retryOptions.Value.MaxRetryCount,
            ProxyRules: proxyConfig.Value.Configurations);

        // directly to PrepareSource — no FetchConfiguration activity
    }

    private string JobId => TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");
}
```

**Delete** the entire `[src/NetworkA/Activities/NetworkA.Activities.JobSetup/](src/NetworkA/Activities/NetworkA.Activities.JobSetup/)` project (class, `.csproj`, `Program.cs`, `appsettings.json`).

Remove `NetworkA.Activities.JobSetup` from the solution file. The `setup-tasks` task queue disappears. Remove the `FetchConfiguration` entry from `WorkflowActivityConfig` in `appsettings.json` on the decomposition workflow host.

---

### 4. Remove the `WorkflowActivityConfig` intermediate layer

The current pipeline is:
`WorkflowActivityConfigOptions` → local activity → `WorkflowActivityConfig` + `ActivityTimeoutConfig` + `RetryPolicyConfig` → `ToActivityOptions`

`ActivityTimeoutEntry` and `ActivityTimeoutConfig` are the same shape. `RetryPolicyEntry` and `RetryPolicyConfig` are the same shape. The middle layer only existed for MongoDB deserialization. Remove it.

**Delete:**

- `[src/Shared/Shared.Infrastructure/Activities/WorkflowActivityConfigLocalActivity.cs](src/Shared/Shared.Infrastructure/Activities/WorkflowActivityConfigLocalActivity.cs)`
- `[src/Shared/Shared.Contracts/Models/WorkflowActivityConfig.cs](src/Shared/Shared.Contracts/Models/WorkflowActivityConfig.cs)`
- `[src/Shared/Shared.Contracts/Models/ActivityTimeoutConfig.cs](src/Shared/Shared.Contracts/Models/ActivityTimeoutConfig.cs)`
- `[src/Shared/Shared.Contracts/Models/RetryPolicyConfig.cs](src/Shared/Shared.Contracts/Models/RetryPolicyConfig.cs)`

**Update** `[src/Shared/Shared.Infrastructure/Extensions/ActivityOptionsExtensions.cs](src/Shared/Shared.Infrastructure/Extensions/ActivityOptionsExtensions.cs)` — change the extension target from `WorkflowActivityConfig` to `WorkflowActivityConfigOptions.ActivityTimeoutEntry`:

```csharp
public static ActivityOptions ToActivityOptions(
    this WorkflowActivityConfigOptions.ActivityTimeoutEntry cfg,
    string taskQueue) { ... } // same body
```

**Update both workflows** — remove the `await ExecuteLocalActivityAsync(WorkflowActivityConfigLocalActivity...)` call from `RunAsync`, inject `IOptions<WorkflowActivityConfigOptions>` in the constructor, and cache the relevant `WorkflowConfigEntry`:

```csharp
public class DecompositionWorkflow(
    IOptions<WorkflowActivityConfigOptions> activityConfig,
    IOptions<ProxyConfigOptions> proxyConfig,
    IOptions<RetryPolicyOptions> retryOptions)
{
    private readonly WorkflowActivityConfigOptions.WorkflowConfigEntry _wfConfig =
        activityConfig.Value.Configs.TryGetValue("decomposition-workflow", out var cfg)
            ? cfg
            : throw new InvalidOperationException("No config for 'decomposition-workflow'");

    private ActivityOptions GetOptions(string activityName, string taskQueue) =>
        _wfConfig.Activities[activityName].ToActivityOptions(taskQueue);
}
```

Same pattern for `AssemblyWorkflow` with key `"assembly-workflow"`.

Remove `WorkflowActivityConfigLocalActivity` registration from both workflow workers' `Program.cs`.

---

### 5. Remove `jobId` from `RetryChunk` and `WriteHardFail`

Neither activity uses `jobId` for path resolution — only for the log message. The workflow already logs the job context via its own structured logs.

**Files to update:**

- `[src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/RetryChunkActivity.cs](src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/RetryChunkActivity.cs)` — signature becomes `RetryChunkAsync(string chunkName)`
- `[src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/WriteHardFailActivity.cs](src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/WriteHardFailActivity.cs)` — signature becomes `WriteHardFailAsync(string chunkName)`; update `.HARDFAIL.txt` content to not reference `jobId`
- `[src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs](src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs)` — update both `ExecuteActivityAsync` call sites in `ChunkRetryRequestedAsync`

---

### 6. Extract `JobId` property in `DecompositionWorkflow`

Currently `WorkflowId.Replace("decomposition-", "")` is repeated twice. Extract to a private property.

```csharp
private string JobId => TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");
```

---

### 7. Remove `CreatedAt` / `UpdatedAt` from `AssemblyBlueprint`

**File:** `[src/Shared/Shared.Contracts/Models/AssemblyBlueprint.cs](src/Shared/Shared.Contracts/Models/AssemblyBlueprint.cs)` — remove both properties.

**File:** `ParseManifestActivities.cs` — remove the `CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow` initializer lines.

---

### 8. Slim down `DecomposeAndSplit` return type — remove 6 blank fields

`DecomposeAndSplitActivities` currently returns a full `DecompositionMetadata` with 6 fields it cannot fill:

```csharp
TargetNetwork:     string.Empty,
CallingSystemId:   string.Empty,
CallingSystemName: string.Empty,
ExternalId:        string.Empty,
AnswerType:        default,
AnswerLocation:    null,
```

The workflow then immediately patches them with `metadata with { ... }` from `request`. This is confusing — the record looks complete but is half-placeholder.

**New file:** `src/Shared/Shared.Contracts/Models/SplitResult.cs`

```csharp
public record SplitResult(
    string PackageType,
    string OriginalPackageName,
    int TotalChunks,
    IReadOnlyList<FileDescriptor> Files,
    IReadOnlyList<string> NestedArchives);
```

**Update** `[src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing/Activities/DecomposeAndSplitActivities.cs](src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing/Activities/DecomposeAndSplitActivities.cs)` — change return type from `DecompositionMetadata` to `SplitResult` and remove the 6 blank fields from the return statement.

**Update** `[src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs](src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs)` — assemble the full `DecompositionMetadata` inline in `RunAsync` after the activity returns:

```csharp
var splitResult = await TemporalWorkflow.ExecuteActivityAsync<SplitResult>(
    "DecomposeAndSplit", [prepared, _config], GetOptions(...));

var manifest = new DecompositionMetadata(
    JobId:             JobId,
    PackageType:       splitResult.PackageType,
    OriginalPackageName: splitResult.OriginalPackageName,
    SourcePath:        _config.SourcePath,
    TargetPath:        _config.TargetPath,
    TargetNetwork:     request.TargetNetwork,
    CallingSystemId:   request.CallingSystemId,
    CallingSystemName: request.CallingSystemName,
    ExternalId:        request.ExternalId,
    TotalChunks:       splitResult.TotalChunks,
    AnswerType:        request.AnswerType,
    AnswerLocation:    request.AnswerLocation,
    Files:             splitResult.Files,
    NestedArchives:    splitResult.NestedArchives);

await TemporalWorkflow.ExecuteActivityAsync("WriteManifest", [manifest], GetOptions(...));
```

`DecompositionMetadata` stays unchanged as the manifest wire format. The `with { }` enrichment block is deleted.

---

### 9. Slim `PrepareSource` signature to `(string jobId, string sourcePath)`

`PrepareSourceAsync` receives the full `WorkflowConfiguration` but only reads two of its five fields. The other three (`TargetPath`, `MaxRetryCount`, `ProxyRules`) are invisible dead weight in this activity's interface.

**Update** `[src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing/Activities/PrepareSourceActivities.cs](src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing/Activities/PrepareSourceActivities.cs)` — change signature to `PrepareSourceAsync(string jobId, string sourcePath)` and replace `config.JobId` / `config.SourcePath` references accordingly.

**Update** `[src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs](src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs)` — update the `ExecuteActivityAsync("PrepareSource", ...)` call to pass `[JobId, _config.SourcePath]` instead of `[_config]`.

---

### 10. Derive `assemblyDir` inside `RepackAndFinalize` from `blueprint`

`AssemblyWorkflow` passes `assemblyDir` as an explicit parameter to `RepackAndFinalize`, but that string is `Path.Combine(blueprint.TargetPath, $"_assembly_{blueprint.Id}")` — the same formula `AssembleFilesActivities` used to create the directory. `RepackAndFinalize` already receives `blueprint` so it can compute this itself, removing a parameter that duplicates information.

**Update** `[src/NetworkB/Activities/NetworkB.Activities.HeavyAssembly/Activities/RepackAndFinalizeActivities.cs](src/NetworkB/Activities/NetworkB.Activities.HeavyAssembly/Activities/RepackAndFinalizeActivities.cs)` — remove `string assemblyDir` parameter; add `var assemblyDir = Path.Combine(blueprint.TargetPath, $"_assembly_{blueprint.Id}");` as the first line of the method.

**Update** `[src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs](src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs)` — change the `ExecuteActivityAsync("RepackAndFinalize", [blueprint, assembleResult.AssemblyDir], ...)` call to `[blueprint]` only.

---

## Resulting Workflow Shapes

**DecompositionWorkflow** (after changes):

```
inline                _wfConfig from constructor-injected WorkflowActivityConfigOptions
inline                _config built from ProxyConfigOptions + RetryPolicyOptions + request
ExecuteActivity       PrepareSource(jobId, sourcePath)   (heavy-processing-tasks)  ← slimmed
ExecuteActivity       DecomposeAndSplit → SplitResult    (heavy-processing-tasks)  ← slimmed
inline                DecompositionMetadata assembled from SplitResult + _config + request
ExecuteActivity       WriteManifest          (manifest-tasks)
WaitCondition         _callbackReceived
Signal: ChunkRetryRequestedAsync → RetryChunk(chunkName) or WriteHardFail(chunkName)
```

**AssemblyWorkflow** (after changes):

```
inline                _wfConfig from constructor-injected WorkflowActivityConfigOptions
WaitCondition         _manifestReceived || _manifestHardFailed
ExecuteActivity       ParseManifest          (manifest-assembly-tasks)  ← renamed
WaitCondition         all chunks resolved
ExecuteActivity       AssembleFiles          (heavy-assembly-tasks)
ExecuteActivity       RepackAndFinalize(blueprint)        (heavy-assembly-tasks)    ← assemblyDir removed
                      ← UpdateBlueprintStatus removed
ExecuteActivity       WriteCsvReport         (callback-dispatch-tasks)
ExecuteActivity       DispatchAnswer         (callback-dispatch-tasks)
ExecuteActivity       UpdateClientA          (callback-dispatch-tasks)
```

---

## What Does NOT Change

- `WorkflowActivityConfigOptions` — stays as the appsettings binding model; `ActivityOptionsExtensions` stays but targets `ActivityTimeoutEntry` directly
- `DecompositionMetadata` — stays unchanged as the manifest wire format (JSON on disk)
- `WriteManifest`, `AssembleFiles`, `WriteCsvReport`, `DispatchAnswer`, `UpdateClientA` — all have real I/O and remain remote activities with unchanged signatures
- Task queues: `heavy-processing-tasks`, `heavy-assembly-tasks`, `manifest-tasks`, `manifest-assembly-tasks`, `callback-dispatch-tasks`, `retry-dispatch-tasks` — all still valid (`setup-tasks` is removed)
- Signal handlers in both workflows — unchanged
