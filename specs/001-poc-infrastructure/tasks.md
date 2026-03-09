# Tasks: POC Infrastructure Setup

**Feature**: `001-poc-infrastructure`  
**Input**: Design documents from `/specs/001-poc-infrastructure/`  
**Prerequisites**: plan.md ✅, spec.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅  
**Tests**: PROHIBITED (Constitution Principle I — no test tasks generated)

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel (different files, no inter-task dependencies)
- **[Story]**: User story label — US1/US2/US3 (setup and foundational phases omit this)
- All paths are relative to repository root

---

## Phase 1: Setup — Template Cleanup (FR-001)

> **CRITICAL**: Must complete before ANY POC code is written.

**Purpose**: Strip all .NET template boilerplate from the 14 pre-generated project scaffolds so every `Program.cs` starts clean.

- [x] T001 Clean up NetworkA.Ingestion.API — delete `WeatherForecast.cs`, remove `app.MapGet("/weatherforecast", ...)`, `builder.Services.AddOpenApi()`, `app.MapOpenApi()`, `app.UseHttpsRedirection()` from `Program.cs`, and remove `Microsoft.AspNetCore.OpenApi` `<PackageReference>` from `.csproj` in `src/NetworkA/NetworkA.Ingestion.API/`
- [x] T002 [P] Clean up NetworkA.Callback.Receiver — same template removals as T001 in `src/NetworkA/NetworkA.Callback.Receiver/`
- [x] T003 [P] Delete placeholder `Worker.cs` (auto-generated `BackgroundService`) and clear `Program.cs` to empty host skeleton for all 10 worker/listener projects: `NetworkA.Decomposition.Workflow`, `NetworkA.Activities.JobSetup`, `NetworkA.Activities.HeavyProcessing`, `NetworkA.Activities.Manifest`, `NetworkA.Activities.Dispatch`, `NetworkB.ProxyListener.Service`, `NetworkB.Assembly.Workflow`, `NetworkB.Activities.ManifestState`, `NetworkB.Activities.HeavyAssembly`, `NetworkB.Activities.Reporting`
- [x] T004 [P] Delete `Class1.cs` from `src/Shared/Shared.Contracts/` and `src/Shared/Shared.Infrastructure/`
- [x] T005 [P] Reduce `appsettings.json` to a project-specific skeleton (no ASP.NET template defaults) and remove or empty `appsettings.Development.json` across all 14 projects in `src/`

**Checkpoint**: All 14 projects compile with no template code remaining.

---

## Phase 2: Foundational — Shared Contracts & Infrastructure

> **⚠️ CRITICAL**: No user story work can begin until this phase is complete.

**Purpose**: Define all enums, DTOs, interfaces, signal records, options classes, and DI extension methods that every microservice depends on.

### Shared.Contracts — Enums

- [x] T006 Create `AnswerType.cs`, `JobStatus.cs`, and `FileTransferStatus.cs` enums in `src/Shared/Shared.Contracts/Enums/` per data-model.md §1.1–1.3

### Shared.Contracts — Payloads & Models

- [x] T007 [P] Create `IngestionRequestPayload.cs` and `StatusCallbackPayload.cs` in `src/Shared/Shared.Contracts/Payloads/` per data-model.md §1.4–1.5 (immutable — fields must not be altered)
- [x] T008 [P] Create shared value type records `FileDescriptor.cs`, `ConvertedFileDescriptor.cs`, `ChunkDescriptor.cs`, and `FileResult.cs` in `src/Shared/Shared.Contracts/Models/` per contracts/interfaces.md Shared Value Types section
- [x] T009 [P] Create domain model classes `Job.cs`, `ProxyConfiguration.cs`, and `AssemblyBlueprint.cs` in `src/Shared/Shared.Contracts/Models/` per data-model.md §2.1–2.2 and §4.1 (plain C# types — no MongoDB or Temporalio attributes)
- [x] T010 [P] Create `WorkflowConfiguration.cs` and `DecompositionMetadata.cs` records in `src/Shared/Shared.Contracts/Models/` per contracts/interfaces.md activity interface definitions — `DecompositionMetadata` includes `AnswerType` and `AnswerLocation` fields (see §IHeavyProcessingActivities)

### Shared.Contracts — Signals

- [x] T011 [P] Create Temporal signal records `ChunkSignal.cs`, `ManifestSignal.cs`, `UnsupportedFileSignal.cs`, `HardFailSignal.cs`, and `CallbackSignal.cs` in `src/Shared/Shared.Contracts/Signals/` per data-model.md §5

### Shared.Contracts — Interfaces

- [x] T012 Create repository interfaces `IJobRepository.cs`, `IProxyConfigRepository.cs`, and `IAssemblyBlueprintRepository.cs` in `src/Shared/Shared.Contracts/Interfaces/` per contracts/interfaces.md (no `[Activity]` attributes — no Temporalio NuGet reference in this project)
- [x] T013 [P] Create cache and service interfaces `IProxyConfigCache.cs`, `IIngestionService.cs`, `ICallbackService.cs`, `INetworkAClient.cs`, `IAnswerDispatcher.cs`, and `ICsvReportWriter.cs` in `src/Shared/Shared.Contracts/Interfaces/` per contracts/interfaces.md
- [x] T014 [P] Create activity interfaces `IJobSetupActivities.cs`, `IHeavyProcessingActivities.cs`, `IManifestActivities.cs`, `IDispatchActivities.cs`, `IManifestStateActivities.cs`, `IHeavyAssemblyActivities.cs`, and `IReportingActivities.cs` in `src/Shared/Shared.Contracts/Interfaces/` per contracts/interfaces.md (no `[Activity]` attributes on interfaces)

### Shared.Infrastructure — Options & Extensions

- [x] T015 Create all options classes in `src/Shared/Shared.Infrastructure/Options/`: `MongoDbOptions.cs`, `RedisOptions.cs`, `RabbitMqOptions.cs`, `TemporalOptions.cs`, `OutboxOptions.cs`, `RetryPolicyOptions.cs`, `MockOptions.cs`, `ProxyListenerOptions.cs`, `NetworkACallbackOptions.cs`, `InboxOptions.cs`, `AssemblyOptions.cs` per data-model.md §6
- [x] T016 [P] Implement `MongoDbServiceExtensions.cs` (registers `IMongoClient` and `IMongoDatabase` using `MongoDbOptions` from configuration) in `src/Shared/Shared.Infrastructure/Extensions/`
- [x] T017 [P] Implement `RedisServiceExtensions.cs` (registers `IConnectionMultiplexer` using `RedisOptions` from configuration) in `src/Shared/Shared.Infrastructure/Extensions/`
- [x] T018 [P] Implement `TemporalServiceExtensions.cs` (registers Temporal client using `TemporalOptions` from configuration — namespace `default`, gRPC address from `TargetHost`) in `src/Shared/Shared.Infrastructure/Extensions/`

**Checkpoint**: Shared.Contracts and Shared.Infrastructure compile without errors. All 14 services can reference them.

---

## Phase 3: User Story 1 — End-to-End Happy Path (Priority: P1) 🎯 MVP

**Goal**: Submit a valid `IngestionRequestPayload` via HTTP and observe the complete flow execute through all 14 services — from Network A ingestion, Temporal orchestration, mock chunk/manifest writing, through Network B assembly and reporting — ending with a `StatusCallbackPayload { JobStatus: COMPLETED }` delivered to the configured answer location.

**Independent Test**: Start `docker-compose up -d`, build solution, start all services per quickstart.md §Step 3. Submit the curl in quickstart.md §Step 4. Verify in Temporal UI (http://localhost:8233) that `decomposition-{jobId}` workflow progresses through all activities, `assembly-{jobId}` workflow completes, and the final `COMPLETED` status is written to the configured answer location.

### Network A: Ingestion API

- [x] T019 [P] [US1] Implement `IngestionRequestValidator.cs` (FluentValidation — all fields required except `AnswerLocation`; `AnswerLocation` required when `AnswerType == FileSystem`) in `src/NetworkA/NetworkA.Ingestion.API/Validators/`
- [x] T020 [P] [US1] Implement `MongoJobRepository.cs` (concrete `IJobRepository` — `FindByIdAsync`, `FindByExternalIdAsync`, `UpsertAsync`, `IncrementChunkRetryCountAsync` using MongoDB.Driver 3.x Builder-based filters on `jobs` collection) in `src/Shared/Shared.Infrastructure/Repositories/` — placed here because Ingestion API, Callback Receiver, and Dispatch Activity all share this implementation; each project references `Shared.Infrastructure` independently
- [x] T021 [US1] Implement `IngestionService.cs` (concrete `IIngestionService` — generates `JobId`, constructs `Job`, persists via `IJobRepository`, calls `StartWorkflowAsync` on Temporal with workflow ID `decomposition-{jobId}`, task queue `decomposition-workflow`, and `IngestionRequestPayload` as the workflow start parameter; returns `JobId`) in `src/NetworkA/NetworkA.Ingestion.API/Services/`
- [x] T022 [US1] Implement `IngestionController.cs` (`[Route("api/v1/ingestion")] [HttpPost]` — validates payload via `IValidator<IngestionRequestPayload>`, delegates to `IIngestionService`, returns `202 Accepted` with `{ jobId }` or `400` with FluentValidation errors) in `src/NetworkA/NetworkA.Ingestion.API/Controllers/`
- [x] T023 [P] [US1] Implement `RabbitMqIngestionConsumer.cs` (`AsyncEventingBasicConsumer` on `netA.ingestion.queue` — deserializes `IngestionRequestPayload` JSON using `ea.Body.ToArray()`, calls `IIngestionService.StartJobAsync`, explicit ack; declare exchange/queue idempotently on startup) in `src/NetworkA/NetworkA.Ingestion.API/Consumers/`
- [x] T024 [US1] Wire up `NetworkA.Ingestion.API/Program.cs` — add controllers, FluentValidation, Serilog (`RenderedCompactJsonFormatter` to console), MongoDB via `AddMongoDb`, register `IJobRepository → MongoJobRepository`, Temporal client via `AddTemporalClient`, RabbitMQ topology declaration + `RabbitMqIngestionConsumer` as `IHostedService`; populate `appsettings.json` with `MongoDB`, `RabbitMq`, `Temporal`, `RetryPolicy` config keys per quickstart.md

### Network A: Decomposition Workflow

- [x] T025 [US1] Implement `DecompositionWorkflow.cs` (`[Workflow]` class on task queue `decomposition-workflow`, workflow ID `decomposition-{jobId}`; start parameter: `IngestionRequestPayload`) — activity sequence: `FetchConfigurationAsync(jobId)` (setup-tasks) → `DecomposeAndSplitAsync(config)` (heavy-processing-tasks) → enrich returned `DecompositionMetadata` with `AnswerType` and `AnswerLocation` from the start-parameter payload using a `with` expression → `WriteManifestAsync(enrichedMetadata)` (manifest-tasks) → `await WaitConditionAsync(() => _callbackReceived)`; in-memory state: `_callbackReceived`, `Dictionary<string, int> _chunkRetryCounts`; signal handlers `[WorkflowSignal] FinalStatusReceivedAsync(StatusCallbackPayload)` sets `_callbackReceived = true`; `[WorkflowSignal] ChunkRetryRequestedAsync(string chunkName)` increments `_chunkRetryCounts[chunkName]`, checks against `MaxRetryCount`, calls `RetryChunkAsync` or `WriteHardFailAsync` accordingly) in `src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/`
- [x] T026 [US1] Wire up `NetworkA.Decomposition.Workflow/Program.cs` — `AddHostedTemporalWorker("decomposition-workflow")`, register `DecompositionWorkflow`; Serilog; populate `appsettings.json` with `Temporal` config key per quickstart.md

### Network A: JobSetup Activity Worker

- [x] T027 [P] [US1] Implement `MongoProxyConfigRepository.cs` (concrete `IProxyConfigRepository` — `FindBySourceFormatAsync` and `GetAllAsync` using MongoDB.Driver Builder filters on `proxy_configurations` collection) in `src/NetworkA/Activities/NetworkA.Activities.JobSetup/Repositories/`
- [x] T028 [P] [US1] Implement `RedisProxyConfigCache.cs` (concrete `IProxyConfigCache` — `GetAsync` with 30-minute TTL: check Redis key `v1:proxyconfig:{sourceFormat}`, on miss fetch from `IProxyConfigRepository` and cache; `InvalidateAsync` deletes key) in `src/NetworkA/Activities/NetworkA.Activities.JobSetup/Cache/`
- [x] T029 [US1] Implement `JobSetupActivities.cs` (`[Activity] FetchConfigurationAsync(string jobId)` — mock: reads `MockChunkCount` and `MaxRetryCount` from injected `MockOptions`/`RetryPolicyOptions`, calls `IProxyConfigCache.GetAsync` for each format, returns hardcoded `WorkflowConfiguration` with `ProxyRules` matching proxy-requirements CSV structure; log connectivity confirmation) in `src/NetworkA/Activities/NetworkA.Activities.JobSetup/Activities/`
- [x] T030 [US1] Wire up `NetworkA.Activities.JobSetup/Program.cs` — `AddHostedTemporalWorker("setup-tasks")`, register `JobSetupActivities`, MongoDB via `AddMongoDb`, Redis via `AddRedis`, Serilog; populate `appsettings.json` with `MongoDB`, `Redis`, `Temporal`, `RetryPolicy`, `Mock` config keys per quickstart.md

### Network A: HeavyProcessing Activity Worker

- [x] T031 [US1] Implement `HeavyProcessingActivities.cs` (`[Activity] DecomposeAndSplitAsync(WorkflowConfiguration config)` — mock: for each mock file entry, produce `config.MockChunkCount` chunks per converted file; write empty `.bin` files named `{jobId}_chunk_{n}.bin` to `OutboxOptions.DataOutboxPath`; return `DecompositionMetadata` with full file→convertedFile→chunk hierarchy, `originalFormat`, `appliedConversion`, `originalRelativePath` per file; pseudo-code comments for actual split/conversion; chunk count per converted file = `MockChunkCount`, `TotalChunks` = sum across all converted files) in `src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing/Activities/`
- [x] T032 [US1] Wire up `NetworkA.Activities.HeavyProcessing/Program.cs` — `AddHostedTemporalWorker("heavy-processing-tasks")`, register `HeavyProcessingActivities`, Serilog; populate `appsettings.json` with `Temporal`, `Outbox`, `Mock` config keys per quickstart.md

### Network A: Manifest Activity Worker

- [x] T033 [US1] Implement `ManifestActivities.cs` (`[Activity] WriteManifestAsync(DecompositionMetadata metadata)` — serialize metadata to hierarchical JSON matching schema in data-model.md §3.2: root fields `jobId`, `packageType`, `originalPackageName`, `targetPath`, `totalChunks`, `answerType`, `answerLocation`, `files[]` with nested `convertedFiles[]` and `chunks[]`; write to `{OutboxOptions.ManifestOutboxPath}/{jobId}_manifest.json`) in `src/NetworkA/Activities/NetworkA.Activities.Manifest/Activities/`
- [x] T034 [US1] Wire up `NetworkA.Activities.Manifest/Program.cs` — `AddHostedTemporalWorker("manifest-tasks")`, register `ManifestActivities`, Serilog; populate `appsettings.json` with `Temporal`, `Outbox` config keys per quickstart.md

### Network A: Callback Receiver

- [x] T035 [US1] Implement `CallbackService.cs` (concrete `ICallbackService` — `HandleFinalStatusAsync`: looks up `Job.TemporalWorkflowId` from `IJobRepository` by `OrigJobId`, signals `FinalStatusReceivedAsync` on the workflow; `HandleChunkRetryRequestAsync`: signals `ChunkRetryRequestedAsync(chunkName)`) in `src/NetworkA/NetworkA.Callback.Receiver/Services/`
- [x] T036 [US1] Implement `CallbackController.cs` (`[Route("api/v1/callbacks")]` — `[HttpPost("status")]` → `ICallbackService.HandleFinalStatusAsync`, returns `204`; `[HttpPost("retry")]` → `ICallbackService.HandleChunkRetryRequestAsync`, returns `204`; returns `400` if `OrigJobId` missing or job not found) in `src/NetworkA/NetworkA.Callback.Receiver/Controllers/`
- [x] T037 [US1] Wire up `NetworkA.Callback.Receiver/Program.cs` — controllers, Serilog, MongoDB via `AddMongoDb`, register `IJobRepository → MongoJobRepository`, Temporal client via `AddTemporalClient`; populate `appsettings.json` with `MongoDB`, `Temporal` config keys per quickstart.md

### Network B: Proxy Event Listener

- [x] T038 [US1] Implement `ProxyEventConsumer.cs` (`AsyncEventingBasicConsumer` on `proxy.events.queue` — extract filename from `filePath` JSON field using `ea.Body.ToArray()`; parse `jobId` as substring before first `_`; route by filename suffix: `.ERROR.txt` → HTTP POST to `/api/v1/callbacks/retry` via `INetworkAClient`; `.UNSUPPORTED.txt` → `SignalWithStart` `UnsupportedFileAsync`; `_manifest.json` → `SignalWithStart` `ManifestArrivedAsync`; `.HARDFAIL.txt` → `SignalWithStart` `HardFailAsync`; else (data chunk) → `SignalWithStart` `ChunkArrivedAsync`; all `SignalWithStart` calls use workflow ID `assembly-{jobId}` and task queue `assembly-workflow`; declare exchange/queue idempotently on startup) in `src/NetworkB/NetworkB.ProxyListener.Service/Consumers/`
- [x] T039 [US1] Wire up `NetworkB.ProxyListener.Service/Program.cs` — Serilog, Temporal client via `AddTemporalClient`, `IHttpClientFactory` for `INetworkAClient`, RabbitMQ `ProxyEventConsumer` as `IHostedService`; populate `appsettings.json` with `Temporal`, `RabbitMq` (ProxyListener keys), `NetworkA` callback base URL, `Assembly:TimeoutMinutes` per quickstart.md

### Network B: Assembly Workflow

- [x] T040 [US1] Implement `AssemblyWorkflow.cs` (`[Workflow]` class on task queue `assembly-workflow`, workflow ID `assembly-{origJobId}`) — in-memory state: `_receivedChunkPaths`, `_hardFailedChunkNames`, `_unsupportedChunkNames`, `_manifestReceived`, `_expectedChunks`; signal handlers `[WorkflowSignal] ManifestArrivedAsync(string)`, `ChunkArrivedAsync(string)`, `UnsupportedFileAsync(string)`, `HardFailAsync(HardFailSignal)`; run body: `WaitConditionAsync(() => _manifestReceived)` → `ParseAndPersistManifestAsync` (sets `_expectedChunks`) → `bool allArrived = WaitConditionAsync(() => resolved >= _expectedChunks, assemblyTimeout)` → `AssembleAndValidateAsync(blueprint, _receivedChunkPaths)` → `GenerateAndDispatchReportAsync(blueprint, fileResults, status)` where `status` = `Timeout` if `!allArrived`, else `Completed`/`CompletedPartially`/`Failed` based on file results; per FR-015: do NOT use `WorkflowRunTimeout`) in `src/NetworkB/NetworkB.Assembly.Workflow/Workflows/`
- [x] T041 [US1] Wire up `NetworkB.Assembly.Workflow/Program.cs` — `AddHostedTemporalWorker("assembly-workflow")`, register `AssemblyWorkflow`, Serilog; populate `appsettings.json` with `Temporal` config key per quickstart.md

### Network B: ManifestState Activity Worker

- [x] T042 [P] [US1] Implement `MongoManifestRepository.cs` (concrete `IAssemblyBlueprintRepository` — `FindByJobIdAsync` and `UpsertAsync` on `assembly_blueprints` collection in Network B MongoDB using MongoDB.Driver 3.x Builder filters) in `src/NetworkB/Activities/NetworkB.Activities.ManifestState/Repositories/`
- [x] T043 [US1] Implement `ManifestStateActivities.cs` (`[Activity] ParseAndPersistManifestAsync(string manifestFilePath)` — mock: read `{jobId}_manifest.json` from disk, deserialize hierarchical JSON per data-model.md §3.2, construct `AssemblyBlueprint` (populate all fields including `Files` hierarchy, `TotalChunks`, `AnswerType`, `AnswerLocation`, `Status = "Aggregating"`), persist via `IAssemblyBlueprintRepository.UpsertAsync`, return blueprint; pseudo-code comments for actual parsing validation) in `src/NetworkB/Activities/NetworkB.Activities.ManifestState/Activities/`
- [x] T044 [US1] Wire up `NetworkB.Activities.ManifestState/Program.cs` — `AddHostedTemporalWorker("manifest-assembly-tasks")`, register `ManifestStateActivities`, MongoDB via `AddMongoDb`, Serilog; populate `appsettings.json` with `Temporal`, `MongoDB` (networkB_db) config keys per quickstart.md

### Network B: HeavyAssembly Activity Worker

- [x] T045 [US1] Implement `HeavyAssemblyActivities.cs` (`[Activity] AssembleAndValidateAsync(AssemblyBlueprint blueprint, IReadOnlyList<string> receivedChunkPaths)` — mock: for each `FileDescriptor` in blueprint, for each `ConvertedFileDescriptor`, sort chunks by `Index` (pseudo-code: concatenate bytes), reverse `AppliedConversion` (pseudo-code: `DirectToProxy`/`PNG`/`DOCX_AND_PNG`/`TXT` reversal comments), write to `OriginalRelativePath` under `TargetPath`; if `blueprint.PackageType` is archive type re-pack (pseudo-code); mark `UnsupportedChunkNames` as `NotSupported`, `HardFailedChunkNames` as `Failed`, rest as `Completed`; return `IReadOnlyList<FileResult>` per data-model.md §3.3) in `src/NetworkB/Activities/NetworkB.Activities.HeavyAssembly/Activities/`
- [x] T046 [US1] Wire up `NetworkB.Activities.HeavyAssembly/Program.cs` — `AddHostedTemporalWorker("heavy-assembly-tasks")`, register `HeavyAssemblyActivities`, Serilog; populate `appsettings.json` with `Temporal` config key per quickstart.md

### Network B: Reporting Activity Worker

- [x] T047 [P] [US1] Implement `CsvReportWriter.cs` (concrete `ICsvReportWriter` — `WriteAsync(string outputPath, IReadOnlyList<FileResult> results)` writes `status,dir_path` CSV with values `COMPLETED`/`FAILED`/`NOT_SUPPORTED` per data-model.md §4.2; file named `{jobId}_report.csv`) in `src/NetworkB/Activities/NetworkB.Activities.Reporting/Services/`
- [x] T048 [P] [US1] Implement `NetworkAHttpClient.cs` (concrete `INetworkAClient` — `SendFinalStatusAsync` HTTP POSTs `StatusCallbackPayload` JSON to `{NetworkACallbackBaseUrl}/api/v1/callbacks/status`; `SendRetryRequestAsync` HTTP POSTs `{ origJobId, chunkName }` to `{NetworkACallbackBaseUrl}/api/v1/callbacks/retry`; uses `IHttpClientFactory`) in `src/NetworkB/Activities/NetworkB.Activities.Reporting/Services/`
- [x] T049 [P] [US1] Implement `RabbitMqAnswerDispatcher.cs`, `FileSystemAnswerDispatcher.cs`, and `AnswerDispatcherFactory.cs` (concrete `IAnswerDispatcher` implementations — RabbitMQ publishes `StatusCallbackPayload` JSON to `netA.callbacks.queue`; FileSystem writes payload JSON to `payload.AnswerLocation`; factory returns correct implementation based on `payload.AnswerType`) in `src/NetworkB/Activities/NetworkB.Activities.Reporting/Services/`
- [x] T050 [US1] Implement `ReportingActivities.cs` (`[Activity] GenerateAndDispatchReportAsync(AssemblyBlueprint blueprint, IReadOnlyList<FileResult> fileResults, JobStatus finalStatus)` — writes CSV via `ICsvReportWriter`; constructs `StatusCallbackPayload` mapping `blueprint.Id → OrigJobId`, `blueprint.AnswerType`, `blueprint.AnswerLocation`, and `finalStatus`; dispatches via `IAnswerDispatcher` (factory selects RabbitMQ or FileSystem based on `payload.AnswerType`); sends HTTP acknowledgment via `INetworkAClient.SendFinalStatusAsync`) in `src/NetworkB/Activities/NetworkB.Activities.Reporting/Activities/`
- [x] T051 [US1] Wire up `NetworkB.Activities.Reporting/Program.cs` — `AddHostedTemporalWorker("callback-dispatch-tasks")`, register `ReportingActivities`, `ICsvReportWriter`, `IAnswerDispatcher` (via `AnswerDispatcherFactory`), `INetworkAClient` (`NetworkAHttpClient`), `IHttpClientFactory`, RabbitMQ connection, Serilog; populate `appsettings.json` with `Temporal`, `RabbitMq`, `NetworkA:CallbackBaseUrl`, `InboxOptions` per quickstart.md

**Checkpoint**: Submit test ingestion request per quickstart.md §Step 4. Temporal UI shows `decomposition-{jobId}` and `assembly-{jobId}` both complete. Final status written to answer location. SC-001 satisfied.

---

## Phase 4: User Story 2 — Proxy Failure and Retry Flow (Priority: P2)

**Goal**: Demonstrate retry path — a simulated `.ERROR.txt` file triggers Network B → Network A retry, DispatchActivities re-queues the chunk, retry counter increments in MongoDB; when exhausted, `.HARDFAIL.txt` is written and Assembly Workflow marks the slot as permanently failed.

**Independent Test**: Start all services. While a job is in progress, publish `{ "filePath": "/network-b/inbox/{jobId}_chunk_1.bin.ERROR.txt" }` to `proxy.events` exchange per quickstart.md §Step 5. Verify: Callback Receiver receives retry request → Decomposition Workflow receives `ChunkRetryRequested` signal → Dispatch Activity writes chunk to DataOutboxPath → MongoDB `ChunkRetryCounters` incremented. Repeat until `MaxRetryCount` exhausted → verify `.HARDFAIL.txt` appears in DataOutboxPath.

- [x] T052 [US2] Implement `DispatchActivities.cs` — `[Activity] RetryChunkAsync(string jobId, string chunkName)`: re-writes mock chunk `.bin` file to `OutboxOptions.DataOutboxPath`, calls `IJobRepository.IncrementChunkRetryCountAsync`; `[Activity] WriteHardFailAsync(string jobId, string chunkName)`: writes `{jobId}_{chunkName}.HARDFAIL.txt` to `OutboxOptions.DataOutboxPath` per data-model.md §3.1 naming convention in `src/NetworkA/Activities/NetworkA.Activities.Dispatch/Activities/`
- [x] T053 [US2] Wire up `NetworkA.Activities.Dispatch/Program.cs` — `AddHostedTemporalWorker("retry-dispatch-tasks")`, register `DispatchActivities`, MongoDB via `AddMongoDb`, register `IJobRepository → MongoJobRepository`, Serilog; populate `appsettings.json` with `Temporal`, `MongoDB`, `Outbox`, `RetryPolicy` config keys per quickstart.md
- [x] T054 [US2] Verify `DecompositionWorkflow.cs` retry orchestration (already scaffolded in T025) — confirm `ChunkRetryRequestedAsync` increments `_chunkRetryCounts[chunkName]`, calls `RetryChunkAsync` (retry-dispatch-tasks) when count ≤ `MaxRetryCount`, and `WriteHardFailAsync` (retry-dispatch-tasks) when limit is exhausted; `MaxRetryCount` is sourced from `WorkflowConfiguration.MaxRetryCount` returned by `FetchConfigurationAsync` in `src/NetworkA/NetworkA.Decomposition.Workflow/Workflows/DecompositionWorkflow.cs`

**Checkpoint**: Retry flow demonstrates SC-003 — chunk re-queued, retry counter incremented, HARDFAIL written on exhaustion.

---

## Phase 5: User Story 3 — Infrastructure Connectivity Verification (Priority: P3)

**Goal**: All 14 microservices emit structured startup log lines confirming each infrastructure connection. Misconfigured services fail fast with a descriptive error and non-zero exit code.

**Independent Test**: Start `docker-compose up -d`. Start all services. Inspect startup logs and confirm the expected `[INF] <ServiceName>: <Dependency> connected successfully` lines per quickstart.md §Step 3 expected output. Then stop docker-compose, start one service, verify it logs a clear error and exits non-zero (SC-004 startup validation).

- [x] T055 [P] [US3] Add structured startup log lines to all Network A services confirming: MongoDB connection (`Ingestion.API`, `Callback.Receiver`, `Activities.JobSetup`, `Activities.Dispatch`), Redis connection (`Activities.JobSetup`), RabbitMQ connection (`Ingestion.API`), Temporal worker registration with task queue name (all workflow/activity workers) — format: `[INF] {ServiceName}: {Dependency} connected successfully` — FR-021, SC-002
- [x] T056 [P] [US3] Add structured startup log lines to all Network B services confirming: MongoDB connection (`Activities.ManifestState`), RabbitMQ connection (`ProxyListener.Service`, `Activities.Reporting`), Temporal worker registration with task queue name (all workflow/activity workers) — FR-021, SC-002
- [x] T057 [US3] Implement fail-fast startup validation in all 14 services — wrap infrastructure connection attempts in startup; on failure log `[ERR] {ServiceName}: Failed to connect to {Dependency}: {message}` and return non-zero exit code (per spec Acceptance Scenario 4, SC-004)
- [x] T058 [P] [US3] Audit and finalize all 14 `appsettings.json` files — verify each service has the complete set of configuration keys from quickstart.md §Configuration table with no hardcoded connection strings, paths, or credentials anywhere in source — FR-019, SC-006

**Checkpoint**: All services log successful connectivity on start. Missing config causes clean early exit.

---

## Final Phase: Polish & Cross-Cutting Concerns

**Purpose**: Validate the full system end-to-end against all success criteria.

- [x] T059 [P] Run `dotnet build Dintinct.sln` and resolve any compilation errors across all 14 projects — SC-004
- [x] T060 [P] Audit all 14 `Program.cs` and service registration files — confirm every dependency is registered and consumed via interface type (no concrete class injected directly) — FR-020
- [x] T061 [P] Search all `src/` files for hardcoded literals (connection strings, directory paths, credentials, magic chunk counts) and replace with configuration-bound values — FR-019, SC-006
- [x] T062 Run full quickstart.md validation — `docker-compose up -d`, `dotnet build`, start all 14 services, submit ingestion request per §Step 4, verify `COMPLETED` status in ≤60 seconds, verify CSV report matches `output.csv` format — SC-001, SC-002, SC-004, SC-005

---

## Dependencies & Execution Order

### Phase Dependencies

| Phase | Depends On | Blocks |
|-------|-----------|--------|
| Phase 1 — Setup | None | Phase 2 |
| Phase 2 — Foundational | Phase 1 | All user story phases |
| Phase 3 — US1 (P1) | Phase 2 | Phase 4 (US2 extends US1 workflow + dispatch worker) |
| Phase 4 — US2 (P2) | Phase 3 (T025 DecompositionWorkflow, T037 Callback wiring) | Final Phase |
| Phase 5 — US3 (P3) | Phase 3 (all Program.cs wiring tasks) | Final Phase |
| Final Phase | Phases 3, 4, 5 | — |

### User Story Dependencies

- **US1 (P1)**: No dependencies on other stories — pure happy path; all 14 services must be compilable
- **US2 (P2)**: Depends on US1 — extends `DecompositionWorkflow.cs` (T025) and adds `DispatchActivities` + Callback `/retry` endpoint
- **US3 (P3)**: Depends on US1 — adds startup log lines and fail-fast validation to already-wired services

### Within Phase 3 (US1) — Execution Order

```
T019–T020 [P]    → Validator + MongoJobRepository (no dependencies)
T021             → IngestionService (depends on T020 for IJobRepository)
T022             → IngestionController (depends on T021)
T023 [P]         → RabbitMqIngestionConsumer (depends on T021)
T024             → Program.cs wiring (depends on T021–T023)
T025             → DecompositionWorkflow (depends on Phase 2 interfaces)
T026             → Program.cs wiring (depends on T025)
T027–T028 [P]    → MongoProxyConfigRepository + RedisProxyConfigCache
T029             → JobSetupActivities (depends on T027–T028)
T030             → Program.cs wiring (depends on T029)
T031             → HeavyProcessingActivities (depends on Phase 2 models)
T032             → Program.cs wiring (depends on T031)
T033             → ManifestActivities (depends on Phase 2 models)
T034             → Program.cs wiring (depends on T033)
T035             → CallbackService (depends on T020 for IJobRepository)
T036             → CallbackController (depends on T035)
T037             → Program.cs wiring (depends on T035–T036)
T038             → ProxyEventConsumer (depends on Phase 2 Temporal extensions)
T039             → Program.cs wiring (depends on T038)
T040             → AssemblyWorkflow (depends on Phase 2 interfaces)
T041             → Program.cs wiring (depends on T040)
T042 [P]         → MongoManifestRepository
T043             → ManifestStateActivities (depends on T042)
T044             → Program.cs wiring (depends on T043)
T045             → HeavyAssemblyActivities (depends on Phase 2 models)
T046             → Program.cs wiring (depends on T045)
T047–T049 [P]    → CsvReportWriter + NetworkAHttpClient + AnswerDispatchers
T050             → ReportingActivities (depends on T047–T049)
T051             → Program.cs wiring (depends on T050)
```

### Parallel Opportunities

**Phase 2 (Foundational)**:
- T007–T011 can all run in parallel (different files in Shared.Contracts)
- T012–T014 can run in parallel (different interface files)
- T016–T018 can run in parallel (different DI extension files)

**Phase 3 (US1)**:
- Network A activity workers (T027–T034) are all independent processes — implement in parallel
- Network B activity workers (T042–T051) are all independent — implement in parallel after AssemblyWorkflow (T040)
- Ingestion API, Decomposition Workflow, and Callback Receiver can be wired in parallel

**Phase 5 (US3)**:
- T055–T058 can all run in parallel (different service files)

---

## Parallel Execution Examples

### Phase 2 — Parallel Foundational Tasks

```
Parallel batch 1 — all independent:
  T006  Enums
  T007  Payload DTOs
  T008  Value type records
  T009  Domain models
  T010  WorkflowConfiguration + DecompositionMetadata
  T011  Signal records

Parallel batch 2 — after T006–T011:
  T012  Repository interfaces
  T013  Cache + service interfaces
  T014  Activity interfaces
  T015  Options classes

Parallel batch 3 — after T015:
  T016  MongoDbServiceExtensions
  T017  RedisServiceExtensions
  T018  TemporalServiceExtensions
```

### Phase 3 — Parallel US1 Activity Workers

```
Once T025 (DecompositionWorkflow) and T026 (workflow Program.cs) are done:

Parallel — Network A activity workers:
  T027–T030  JobSetup worker (MongoProxyConfigRepository + RedisProxyConfigCache + JobSetupActivities + Program.cs)
  T031–T032  HeavyProcessing worker (HeavyProcessingActivities + Program.cs)
  T033–T034  Manifest worker (ManifestActivities + Program.cs)

After T021 (IngestionService): parallel
  T022  IngestionController
  T023  RabbitMqIngestionConsumer

After T040 (AssemblyWorkflow) and T041 (workflow Program.cs): parallel
  T042–T044  ManifestState worker
  T045–T046  HeavyAssembly worker
  T047–T051  Reporting worker
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Template Cleanup (T001–T005)
2. Complete Phase 2: Shared.Contracts + Shared.Infrastructure (T006–T018)
3. Complete Phase 3: US1 End-to-End Happy Path (T019–T051)
4. **STOP and VALIDATE**: Submit test ingestion request → observe Temporal UI → verify COMPLETED status
5. Demo the end-to-end happy path

### Incremental Delivery

1. Phase 1 + Phase 2 → Foundation ready (all 14 projects compile)
2. Phase 3 → Add US1 happy path → validate → **MVP demo**
3. Phase 4 → Add US2 retry flow → validate → **retry demo**
4. Phase 5 → Add US3 connectivity verification → validate → **production-readiness signal**
5. Final Phase → Polish and full quickstart validation

---

## Summary

| Phase | Tasks | Story | Key Deliverable |
|-------|-------|-------|-----------------|
| Phase 1: Setup | T001–T005 | — | Clean 14-project scaffold |
| Phase 2: Foundational | T006–T018 | — | All contracts, interfaces, DI extensions |
| Phase 3: US1 Happy Path | T019–T051 | US1 | Full end-to-end flow (SC-001) |
| Phase 4: US2 Retry Flow | T052–T054 | US2 | Retry + hard-fail path (SC-003) |
| Phase 5: US3 Connectivity | T055–T058 | US3 | Startup logs + fail-fast (SC-002, SC-004) |
| Final Phase: Polish | T059–T062 | — | Full quickstart validation |
| **Total** | **62 tasks** | | |

**Parallel opportunities**: 28 tasks marked `[P]`  
**MVP scope**: Phase 1 + Phase 2 + Phase 3 (T001–T051)
