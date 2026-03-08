# Implementation Plan: POC Infrastructure Setup

**Branch**: `001-poc-infrastructure` | **Date**: 2026-03-08 | **Spec**: [spec.md](./spec.md)  
**Input**: Feature specification from `/specs/001-poc-infrastructure/spec.md`

---

## Summary

Build the complete POC infrastructure for a cross-network file transfer system operating across Network A (ingestion/decomposition), a simulated Proxy (file transfer), and Network B (assembly/validation). The POC wires all 14 microservices with real infrastructure connections (Temporal, MongoDB, Redis, RabbitMQ) and mocks all heavy computation (file splitting, format conversion, checksum validation) with pseudo-code stubs. Before any POC code is written, all .NET template boilerplate must be removed from the generated project scaffold.

The implementation begins with **cleanup first**, then proceeds through shared contracts, infrastructure plumbing, and finally each network's services in dependency order.

---

## Technical Context

**Language/Version**: C# 13 / .NET 10  
**Primary Dependencies**: Temporalio 1.x (`Temporalio`, `Temporalio.Extensions.Hosting`), MongoDB.Driver 3.x, StackExchange.Redis 2.x, RabbitMQ.Client 7.x, Serilog (`Serilog.AspNetCore`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Formatting.Compact`, `Serilog.Enrichers.Environment`), FluentValidation  
**Storage**: MongoDB (Network A DB + Network B DB, separate logical databases on the same instance) / Redis (Network A cache only)  
**Testing**: None — tests are prohibited unless explicitly requested (Constitution Principle I)  
**Target Platform**: Linux/Windows server (local machine for POC)  
**Project Type**: Distributed microservices — 14 separate .NET processes  
**Performance Goals**: POC — end-to-end happy path completes within 60 seconds (SC-001)  
**Constraints**: No hardcoded connection strings, paths, or credentials (FR-019, SC-006); mock heavy logic (FR-007/008/009/016/017); configurable chunk count (FR-008)  
**Scale/Scope**: 14 microservices, 2 logical networks, 1 Temporal namespace, 4 infrastructure dependencies

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked post-design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Zero Tests | ✅ PASS | No test projects or test frameworks referenced anywhere. Testing field set to "None". |
| II. Clean Code | ✅ PASS | All mock implementations use pseudo-code comments explaining real logic. No magic strings or numbers. No dead code. Names are intention-revealing. |
| III. Organized Directory Structure | ✅ PASS | All 14 projects already exist in the correct `src/NetworkA/`, `src/NetworkB/`, `src/Shared/` layout per the generator script. No new top-level folders. |
| IV. Technology Stack | ✅ PASS | .NET 10, C# 13, Temporalio, MongoDB.Driver, Redis, RabbitMQ.Client. No alternative ORMs or workflow engines introduced. |
| V. HTTP via ASP.NET Core Controllers | ✅ PASS | Ingestion API and Callback Receiver use `ControllerBase` controllers with `/api/v1/[resource]` routes. `app.MapGet("/weatherforecast", ...)` minimal API routes from template are removed in Step 0. FluentValidation for input validation. |
| VI. Shared Project Boundaries | ✅ PASS | `Shared.Contracts` has only DTOs, enums, interfaces — no infrastructure references. `Shared.Infrastructure` has Temporal client, MongoDB, Redis setup. All workers reference `Shared.Infrastructure`; `Shared.Contracts` does not. |

**Post-design re-check**: All principles remain satisfied. Activity interfaces in `Shared.Contracts` do not carry the `[Activity]` attribute (which would require the Temporalio NuGet) — the attribute is applied on the concrete implementation only.

---

## Project Structure

### Documentation (this feature)

```text
specs/001-poc-infrastructure/
├── plan.md                              # This file
├── research.md                          # Phase 0 output
├── data-model.md                        # Phase 1 output
├── quickstart.md                        # Phase 1 output
├── contracts/
│   ├── http-api.md                      # HTTP endpoint contracts
│   ├── interfaces.md                    # C# interface contracts
│   ├── rabbitmq-topology.md             # RabbitMQ exchange/queue topology
│   └── temporal-task-queues.md          # Temporal task queue and signal contracts
└── tasks.md                             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── Shared/
│   ├── Shared.Contracts/                # DTOs, enums, interfaces (no infra NuGet refs)
│   │   ├── Enums/
│   │   │   ├── AnswerType.cs
│   │   │   ├── JobStatus.cs
│   │   │   └── FileTransferStatus.cs
│   │   ├── Payloads/
│   │   │   ├── IngestionRequestPayload.cs
│   │   │   └── StatusCallbackPayload.cs
│   │   ├── Models/
│   │   │   ├── Job.cs                        ← plain C# type, no MongoDB attributes
│   │   │   ├── ProxyConfiguration.cs         ← plain C# type, no MongoDB attributes
│   │   │   ├── AssemblyBlueprint.cs          ← plain C# type, no MongoDB attributes
│   │   │   ├── FileDescriptor.cs
│   │   │   ├── ConvertedFileDescriptor.cs
│   │   │   ├── ChunkDescriptor.cs
│   │   │   ├── FileResult.cs
│   │   │   ├── WorkflowConfiguration.cs
│   │   │   └── DecompositionMetadata.cs
│   │   ├── Signals/
│   │   │   ├── ChunkSignal.cs
│   │   │   ├── ManifestSignal.cs
│   │   │   ├── UnsupportedFileSignal.cs
│   │   │   ├── HardFailSignal.cs
│   │   │   └── CallbackSignal.cs
│   │   └── Interfaces/
│   │       ├── IJobRepository.cs
│   │       ├── IProxyConfigRepository.cs
│   │       ├── IAssemblyBlueprintRepository.cs
│   │       ├── IProxyConfigCache.cs
│   │       ├── IIngestionService.cs
│   │       ├── ICallbackService.cs
│   │       ├── INetworkAClient.cs
│   │       ├── IAnswerDispatcher.cs
│   │       └── ICsvReportWriter.cs
│   └── Shared.Infrastructure/           # Temporal, MongoDB, Redis setup helpers
│       ├── Extensions/
│       │   ├── MongoDbServiceExtensions.cs
│       │   ├── RedisServiceExtensions.cs
│       │   └── TemporalServiceExtensions.cs
│       ├── Repositories/
│       │   └── MongoJobRepository.cs     ← shared by Ingestion API, Callback Receiver, Dispatch
│       └── Options/
│           ├── MongoDbOptions.cs
│           ├── RedisOptions.cs
│           ├── RabbitMqOptions.cs
│           ├── TemporalOptions.cs
│           ├── OutboxOptions.cs
│           ├── RetryPolicyOptions.cs
│           ├── MockOptions.cs
│           ├── ProxyListenerOptions.cs
│           ├── NetworkACallbackOptions.cs
│           ├── InboxOptions.cs
│           └── AssemblyOptions.cs
├── NetworkA/
│   ├── NetworkA.Ingestion.API/
│   │   ├── Controllers/
│   │   │   └── IngestionController.cs
│   │   ├── Services/
│   │   │   └── IngestionService.cs
│   │   ├── Validators/
│   │   │   └── IngestionRequestValidator.cs
│   │   ├── Consumers/
│   │   │   └── RabbitMqIngestionConsumer.cs
│   │   └── Program.cs
│   ├── NetworkA.Callback.Receiver/
│   │   ├── Controllers/
│   │   │   └── CallbackController.cs
│   │   ├── Services/
│   │   │   └── CallbackService.cs
│   │   └── Program.cs
│   ├── NetworkA.Decomposition.Workflow/
│   │   ├── Workflows/
│   │   │   └── DecompositionWorkflow.cs
│   │   └── Program.cs
│   └── Activities/
│       ├── NetworkA.Activities.JobSetup/
│       │   ├── Activities/
│       │   │   └── JobSetupActivities.cs
│       │   ├── Repositories/
│       │   │   └── MongoProxyConfigRepository.cs
│       │   ├── Cache/
│       │   │   └── RedisProxyConfigCache.cs
│       │   └── Program.cs
│       ├── NetworkA.Activities.HeavyProcessing/
│       │   ├── Activities/
│       │   │   └── HeavyProcessingActivities.cs
│       │   └── Program.cs
│       ├── NetworkA.Activities.Manifest/
│       │   ├── Activities/
│       │   │   └── ManifestActivities.cs
│       │   └── Program.cs
│       └── NetworkA.Activities.Dispatch/
│           ├── Activities/
│           │   └── DispatchActivities.cs
│           └── Program.cs
└── NetworkB/
    ├── NetworkB.ProxyListener.Service/
    │   ├── Consumers/
    │   │   └── ProxyEventConsumer.cs
    │   └── Program.cs
    ├── NetworkB.Assembly.Workflow/
    │   ├── Workflows/
    │   │   └── AssemblyWorkflow.cs
    │   └── Program.cs
    └── Activities/
        ├── NetworkB.Activities.ManifestState/
        │   ├── Activities/
        │   │   └── ManifestStateActivities.cs
        │   ├── Repositories/
        │   │   └── MongoManifestRepository.cs
        │   └── Program.cs
        ├── NetworkB.Activities.HeavyAssembly/
        │   ├── Activities/
        │   │   └── HeavyAssemblyActivities.cs
        │   └── Program.cs
        └── NetworkB.Activities.Reporting/
            ├── Activities/
            │   └── ReportingActivities.cs
            ├── Services/
            │   ├── RabbitMqAnswerDispatcher.cs
            │   ├── FileSystemAnswerDispatcher.cs
            │   ├── AnswerDispatcherFactory.cs
            │   ├── CsvReportWriter.cs
            │   └── NetworkAHttpClient.cs
            └── Program.cs
```

**Structure Decision**: Multi-project microservice layout already established by the generator script. The structure above refines the internal folder layout within each project. No new `.sln` projects will be added.

---

## Implementation Order

> **Critical**: Step 0 (cleanup) must complete before any POC code is written. This is the first task in any task breakdown.

### Step 0 — Template Cleanup (FR-001) ⬅ FIRST

Remove all .NET template boilerplate from the generated scaffold:

**Web API projects** (`NetworkA.Ingestion.API`, `NetworkA.Callback.Receiver`):
- Delete `WeatherForecast` record and `app.MapGet("/weatherforecast", ...)` from `Program.cs`
- Remove `builder.Services.AddOpenApi()` and `app.MapOpenApi()` calls
- Remove `Microsoft.AspNetCore.OpenApi` `<PackageReference>` from `.csproj`
- Remove `app.UseHttpsRedirection()` (POC is HTTP-only)
- Replace `Program.cs` with minimal controller-based host setup

**Worker projects** (all Activity workers + Workflow workers):
- Delete auto-generated `Worker.cs` (the placeholder `BackgroundService` with a 1-second loop)
- Replace `Program.cs` with minimal `IHostApplicationBuilder` + `AddHostedTemporalWorker` setup

**Class library projects** (`Shared.Contracts`, `Shared.Infrastructure`):
- Delete `Class1.cs` (auto-generated placeholder)

**All projects**:
- Reduce `appsettings.json` to a project-specific skeleton (no Microsoft/ASP.NET template defaults)
- Remove `appsettings.Development.json` or reduce to `{}` — all real config in `appsettings.json`

### Step 1 — Shared.Contracts

Define all enums, DTOs, signal records, and interfaces (see `contracts/interfaces.md`).

### Step 2 — Shared.Infrastructure

Implement DI extension methods for MongoDB, Redis, and Temporal client registration.  
Add Serilog packages and configure structured logging helpers.

### Step 3 — Network A: Ingestion API

Implement `IngestionController`, `IngestionService`, `IngestionRequestValidator`, and RabbitMQ ingestion consumer.

### Step 4 — Network A: Decomposition Workflow

Implement `DecompositionWorkflow` with signal handlers and activity orchestration sequence.

### Step 5 — Network A: Activity Workers

Implement each activity worker in dependency order:
1. `JobSetupActivities` (reads from Redis/MongoDB)
2. `HeavyProcessingActivities` (mock chunk writing)
3. `ManifestActivities` (mock manifest writing)
4. `DispatchActivities` (retry and hardfail handling)

### Step 6 — Network A: Callback Receiver

Implement `CallbackController` and `CallbackService` for forwarding signals to the Decomposition Workflow.

### Step 7 — Network B: Proxy Event Listener

Implement `ProxyEventConsumer` with `SignalWithStart` routing logic.

### Step 8 — Network B: Assembly Workflow

Implement `AssemblyWorkflow` with `WaitConditionAsync` aggregation and timeout handling.

### Step 9 — Network B: Activity Workers

Implement each activity worker:
1. `ManifestStateActivities` (mock manifest parsing + MongoDB persistence)
2. `HeavyAssemblyActivities` (mock assembly + validation)
3. `ReportingActivities` (CSV generation + callback dispatch)

---

## Complexity Tracking

No Constitution violations. All principles satisfied without exceptions.

---

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Temporal worker registration | `AddHostedTemporalWorker()` from `Temporalio.Extensions.Hosting` | Integrates cleanly with `IHostApplicationBuilder`; no manual `BackgroundService` boilerplate |
| Assembly Workflow triggering | `SignalWithStart` from Proxy Event Listener | Atomic start-or-signal prevents race conditions on first chunk arrival |
| Stateful aggregation | `Workflow.WaitConditionAsync(() => count >= expected)` | Deterministic suspension without threading primitives |
| Redis cache TTL | 30 minutes for `ProxyConfiguration` | Balances freshness vs. read cost for rule lookups |
| Serilog formatter | `RenderedCompactJsonFormatter` to console | Structured JSON enables log aggregation without grep; human-readable in local dev via Serilog sink rendering |
| MongoDB.Driver LINQ | Builder-based filters only | LINQ2 removed in 3.x; Builder API is explicit and avoids expression tree complexity |
| RabbitMQ message body copy | `ea.Body.ToArray()` before any `await` | Required in RabbitMQ.Client 7.x — `ReadOnlyMemory<byte>` is only valid during the synchronous portion of the handler |
| HTTP vs RabbitMQ for callbacks | HTTP POST as primary, RabbitMQ as secondary (based on `answerType`) | Spec requires both; HTTP is the primary cross-network callback channel; RabbitMQ is the alternative for `answerType == RabbitMQ` |
