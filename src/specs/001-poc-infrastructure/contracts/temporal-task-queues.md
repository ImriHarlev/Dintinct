# Temporal Task Queue Contract

**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

---

## Task Queues

| Task Queue Name | Worker Microservice | Registered Types | Profile |
|----------------|---------------------|------------------|---------|
| `decomposition-workflow` | `NetworkA.Decomposition.Workflow` | `DecompositionWorkflow` (workflow) | Lightweight orchestrator |
| `setup-tasks` | `NetworkA.Activities.JobSetup` | `JobSetupActivities` (activities) | Fast I/O — Redis + MongoDB reads |
| `heavy-processing-tasks` | `NetworkA.Activities.HeavyProcessing` | `HeavyProcessingActivities` (activities) | CPU/Disk intensive — horizontally scalable |
| `manifest-tasks` | `NetworkA.Activities.Manifest` | `ManifestActivities` (activities) | Fast I/O — file write |
| `retry-dispatch-tasks` | `NetworkA.Activities.Dispatch` | `DispatchActivities` (activities) | Disk/Network I/O |
| `assembly-workflow` | `NetworkB.Assembly.Workflow` | `AssemblyWorkflow` (workflow) | Stateful aggregator |
| `manifest-assembly-tasks` | `NetworkB.Activities.ManifestState` | `ManifestStateActivities` (activities) | Fast I/O — parse + MongoDB write |
| `heavy-assembly-tasks` | `NetworkB.Activities.HeavyAssembly` | `HeavyAssemblyActivities` (activities) | CPU/Disk intensive — horizontally scalable |
| `callback-dispatch-tasks` | `NetworkB.Activities.Reporting` | `ReportingActivities` (activities) | Network I/O — HTTP + RabbitMQ/File |

---

## Workflow: `DecompositionWorkflow` (Network A)

**Task Queue**: `decomposition-workflow`  
**Workflow ID pattern**: `decomposition-{jobId}`

### Signals received

| Signal | Handler Method | Payload |
|--------|---------------|---------|
| `FinalStatusReceived` | `FinalStatusReceivedAsync` | `StatusCallbackPayload` |
| `ChunkRetryRequested` | `ChunkRetryRequestedAsync` | `string chunkName` |

### Activity calls (in sequence)

```
1. FetchConfigurationAsync     → Task Queue: setup-tasks
2. DecomposeAndSplitAsync      → Task Queue: heavy-processing-tasks
3. WriteManifestAsync          → Task Queue: manifest-tasks
4. [await FinalStatusReceived signal]
   ↳ on retry signal: RetryChunkAsync / WriteHardFailAsync → Task Queue: retry-dispatch-tasks
```

---

## Workflow: `AssemblyWorkflow` (Network B)

**Task Queue**: `assembly-workflow`  
**Workflow ID pattern**: `assembly-{origJobId}`  
**Started via**: `SignalWithStart` from `NetworkB.ProxyListener.Service`

**Run signature**: `RunAsync(string origJobId, TimeSpan assemblyTimeout)`

The `assemblyTimeout` is passed by the ProxyListener as a workflow run parameter — Temporal workflows cannot use .NET DI or `IConfiguration` directly, so configuration must be injected at start time. The ProxyListener reads `Assembly:TimeoutMinutes` from its own `IConfiguration` and converts it to a `TimeSpan` before calling `StartWorkflowAsync`.

### Signals received

| Signal | Handler Method | Payload |
|--------|---------------|---------|
| `ManifestArrived` | `ManifestArrivedAsync` | `string manifestFilePath` |
| `ChunkArrived` | `ChunkArrivedAsync` | `string chunkFilePath` |
| `UnsupportedFile` | `UnsupportedFileAsync` | `string filePath` |
| `HardFail` | `HardFailAsync` | `HardFailSignal` (`string ChunkName`) |

### Activity calls (in sequence)

```
1. [await WaitConditionAsync(() => _manifestReceived)]
2. ParseAndPersistManifestAsync    → Task Queue: manifest-assembly-tasks
   → sets _expectedChunks = blueprint.TotalChunks
3. bool allArrived = await WaitConditionAsync(
       () => (_receivedCount + _hardFailedCount + _unsupportedCount) >= _expectedChunks,
       timeoutDuration)
   → if allArrived == false: set _timedOut = true
4. AssembleAndValidateAsync(blueprint, receivedChunkPaths)
                                   → Task Queue: heavy-assembly-tasks
5. GenerateAndDispatchReportAsync  → Task Queue: callback-dispatch-tasks
   → JobStatus determined by: _timedOut ? Timeout : any failures ? CompletedPartially/Failed : Completed
```

### Timeout behavior

The timeout is an **internal workflow timer** — implemented via the `timeout` overload of `Workflow.WaitConditionAsync`, which returns `false` when the deadline is exceeded. The workflow then falls through to steps 4 and 5, dispatching the report with `JobStatus.Timeout`.

**Do not use `WorkflowRunTimeout`**: that is a server-side kill switch that terminates the workflow process immediately, preventing steps 4 and 5 from executing.

The timeout duration is read from configuration (e.g., `Assembly:TimeoutMinutes`) and converted to `TimeSpan` inside the workflow.

---

## Namespace

All workers connect to Temporal namespace: `default`  
Server address: `localhost:7233` (from `docker-compose.yml`)
