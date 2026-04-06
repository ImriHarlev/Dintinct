---
name: Retry & Callback Removal
overview: Remove the chunk retry loop and the Network B → Network A HTTP callback path entirely, making both `DecompositionWorkflow` and `AssemblyWorkflow` fire-and-forget. This eliminates the `retry-dispatch-tasks` and the callback portions of `callback-dispatch-tasks`.
todos:
  - id: delete-files
    content: Delete the 8 files/project listed under 'Files to Delete' (RetryChunkActivity, WriteHardFailActivity, RetryPolicyOptions, NetworkACallbackOptions, NetworkA.Callback.Receiver project, UpdateClientAActivities, NetworkAHttpClient, INetworkAClient)
    status: completed
  - id: trim-decomp-workflow
    content: "Trim DecompositionWorkflow.cs: remove _callbackReceived, _chunkRetryCounts, both signal handlers, WaitConditionAsync, MaxRetryCount usage; return after WriteManifest"
    status: completed
  - id: trim-assembly-workflow
    content: "Trim AssemblyWorkflow.cs: remove UpdateClientA activity call and NotifyManifestFailure activity call; hard-fail path just returns"
    status: completed
  - id: trim-proxy-consumer
    content: "Trim ProxyEventConsumer.cs: remove IHttpClientFactory/NetworkACallbackOptions injection; .ERROR.txt branch signals HardFail directly; remove .HARDFAIL.txt special branch"
    status: completed
  - id: trim-config-files
    content: Trim DecompositionConfigLocalActivity.cs (remove MaxRetryCount/RetryPolicyOptions) and WorkflowConfiguration.cs (remove MaxRetryCount parameter)
    status: completed
  - id: trim-programs
    content: "Trim host Program.cs files: NetworkA.Activities.Dispatch (remove retry-dispatch-tasks worker), NetworkB.Activities.Reporting (remove NetworkA HTTP client + UpdateClientAActivities), NetworkA.Decomposition.Workflow (remove RetryPolicy binding)"
    status: completed
  - id: trim-appsettings
    content: "Trim appsettings.json in both workflow hosts: remove RetryPolicy section, RetryChunk/WriteHardFail entries (Network A), and NotifyManifestFailure/UpdateClientA entries (Network B)"
    status: completed
  - id: trim-slnx
    content: Remove NetworkA.Callback.Receiver project entry from Dintinct.slnx
    status: completed
isProject: false
---

# Part 1 — Remove Chunk Retry Logic & Callback Path

## Goal

After this change:

- `DecompositionWorkflow` writes the manifest and returns — no waiting, no retry signals.
- `AssemblyWorkflow` ends after `DispatchAnswer` — no Network A notification.
- Network B signals `HardFail` directly instead of posting to the Network A HTTP callback.

```mermaid
flowchart LR
    DecompWF["DecompositionWorkflow\n(returns after WriteManifest)"]
    ProxyMock["ProxyMockWorker\n(FileSystemWatcher)"]
    ProxyCons["ProxyEventConsumer\n(RabbitMQ)"]
    AssWF["AssemblyWorkflow\n(returns after DispatchAnswer)"]

    DecompWF -->|"chunks + manifest to outbox"| ProxyMock
    ProxyMock -->|"RabbitMQ events"| ProxyCons
    ProxyCons -->|"signals"| AssWF
    ProxyCons -->|".ERROR.txt → HardFail signal (direct)"| AssWF
```

---

## Files to Delete

- [`src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/RetryChunkActivity.cs`](src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/RetryChunkActivity.cs)
- [`src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/WriteHardFailActivity.cs`](src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/WriteHardFailActivity.cs)
- [`src/Shared/Shared.Infrastructure/Options/RetryPolicyOptions.cs`](src/Shared/Shared.Infrastructure/Options/RetryPolicyOptions.cs)
- [`src/Shared/Shared.Infrastructure/Options/NetworkACallbackOptions.cs`](src/Shared/Shared.Infrastructure/Options/NetworkACallbackOptions.cs)
- [`src/NetworkA/NetworkA.Callback.Receiver/`](src/NetworkA/NetworkA.Callback.Receiver/) — entire project (`CallbackController`, `CallbackService`, `ICallbackService`, `ChunkRetryRequest`, etc.)
- [`src/NetworkB/Activities/NetworkB.Activities.Reporting/Activities/UpdateClientAActivities.cs`](src/NetworkB/Activities/NetworkB.Activities.Reporting/Activities/UpdateClientAActivities.cs)
- [`src/NetworkB/Activities/NetworkB.Activities.Reporting/Services/NetworkAHttpClient.cs`](src/NetworkB/Activities/NetworkB.Activities.Reporting/Services/NetworkAHttpClient.cs)
- [`src/NetworkB/Activities/NetworkB.Activities.Reporting/Interfaces/INetworkAClient.cs`](src/NetworkB/Activities/NetworkB.Activities.Reporting/Interfaces/INetworkAClient.cs)

---

## Files to Trim

### [`src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs`](src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs)

Remove:

- Fields: `_callbackReceived`, `_chunkRetryCounts`
- Signal handlers: `FinalStatusReceivedAsync`, `ChunkRetryRequestedAsync`
- `WaitConditionAsync(() => _callbackReceived)` call
- All `MaxRetryCount` references when building `WorkflowConfiguration`
- Activity calls to `RetryChunk` / `WriteHardFail`

Result: after `WriteManifest`, the workflow simply returns.

### [`src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs`](src/NetworkB/NetworkB.Assembly.Workflow/Workflows/AssemblyWorkflow.cs)

Remove:

- `UpdateClientA` activity call at the end of the happy path
- `NotifyManifestFailure` activity call in the manifest hard-fail branch

Result: manifest hard-fail path just returns; happy path ends after `DispatchAnswer`.

### [`src/NetworkB/NetworkB.ProxyListener.Service/Consumers/ProxyEventConsumer.cs`](src/NetworkB/NetworkB.ProxyListener.Service/Consumers/ProxyEventConsumer.cs)

Remove:

- `IHttpClientFactory` and `NetworkACallbackOptions` constructor injections
- HTTP POST to `/api/v1/callbacks/retry` for the ordinary `.ERROR.txt` branch

Change:

- `.ERROR.txt` branch (non-`.HARDFAIL` stem): signal `HardFail` directly on the `AssemblyWorkflow` instead of calling Network A
- Remove the `.HARDFAIL.txt`-stem special-case branch (now redundant)

### [`src/NetworkA/NetworkA.Decomposition.Workflow/Activities/DecompositionConfigLocalActivity.cs`](src/NetworkA/NetworkA.Decomposition.Workflow/Activities/DecompositionConfigLocalActivity.cs)

Remove:

- `IOptions<RetryPolicyOptions>` constructor injection
- `MaxRetryCount` field from `DecompositionRuntimeConfig` and from the `FetchAsync` return value

### [`src/Shared/Shared.Contracts/Models/WorkflowConfiguration.cs`](src/Shared/Shared.Contracts/Models/WorkflowConfiguration.cs)

Remove:

- `MaxRetryCount` parameter from the record

### [`src/NetworkA/Activities/NetworkA.Activities.Dispatch/Program.cs`](src/NetworkA/Activities/NetworkA.Activities.Dispatch/Program.cs)

Remove:

- Entire `retry-dispatch-tasks` worker registration (`RetryChunkActivity`, `WriteHardFailActivity`)
- Any `RetryPolicyOptions` DI binding

### [`src/NetworkB/Activities/NetworkB.Activities.Reporting/Program.cs`](src/NetworkB/Activities/NetworkB.Activities.Reporting/Program.cs)

Remove:

- `Configure<NetworkACallbackOptions>(...)`
- `AddHttpClient("NetworkA")`
- `AddScoped<INetworkAClient, NetworkAHttpClient>()`
- `.AddScopedActivities<UpdateClientAActivities>()` from the `callback-dispatch-tasks` worker

Note: `WriteCsvReportActivities` and `DispatchAnswerActivities` remain on `callback-dispatch-tasks`.

### [`src/NetworkA/NetworkA.Decomposition.Workflow/Program.cs`](src/NetworkA/NetworkA.Decomposition.Workflow/Program.cs)

Remove:

- `Configure<RetryPolicyOptions>(...)` binding (or the entire `RetryPolicy` config section binding)

### [`src/NetworkA/NetworkA.Decomposition.Workflow/appsettings.json`](src/NetworkA/NetworkA.Decomposition.Workflow/appsettings.json)

Remove:

- `RetryPolicy` / `MaxRetryCount` section
- `RetryChunk` and `WriteHardFail` entries from `WorkflowActivityConfig`

### [`src/NetworkB/NetworkB.Assembly.Workflow/appsettings.json`](src/NetworkB/NetworkB.Assembly.Workflow/appsettings.json)

Remove:

- `NotifyManifestFailure` and `UpdateClientA` entries from activity config

### [`Dintinct.slnx`](Dintinct.slnx)

Remove:

- `<Project Path="src/NetworkA/NetworkA.Callback.Receiver/NetworkA.Callback.Receiver.csproj" />` entry
