# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

**Start infrastructure** (RabbitMQ, Temporal, MongoDB, Redis — wait ~15s after):
```powershell
docker-compose up -d
```

**Build all 14 projects:**
```powershell
dotnet build Dintinct.slnx
```

**Run a single service:**
```powershell
dotnet run --project src/NetworkA/NetworkA.Ingestion.API
```

**Submit a test ingestion request:**
```powershell
.\sent-request.ps1
```

**Stop and remove Docker containers:**
```powershell
.\cleanup_docker.ps1
```

**No tests** — tests are prohibited by the project constitution unless explicitly requested.

## Architecture

This is a distributed document processing POC that simulates two air-gapped networks communicating via a proxy.

### Two-Network Model

- **Network A** — ingestion and decomposition: receives a job, splits files into chunks, writes them to an outbox directory, and waits for assembly confirmation.
- **Network B** — assembly and reporting: watches for chunk/manifest arrivals, reassembles files, writes a CSV report, and sends a status callback.

### Technology Roles

| Technology | Role |
|---|---|
| **Temporal.io** (`localhost:7233`) | Durable workflow orchestration for both networks. Each workflow runs as a Temporal worker on a named task queue. |
| **RabbitMQ** (`localhost:5672`) | Cross-network signal bus. Network A writes files to an outbox; a Proxy Event Listener in Network B receives `file.arrived` events and drives assembly. |
| **MongoDB** | Per-network job state. Network A uses `networkA_db`; Network B uses `networkB_db`. |
| **Redis** | Network A only — proxy configuration cache. |

### Outbox / File Protocol

Chunks are named `{jobId}_chunk_{n}.bin` and the manifest is `{jobId}_manifest.json`. The `jobId` prefix lets the Proxy Listener identify the job regardless of file arrival order. An `.ERROR.txt` suffix signals a failed transfer and triggers a retry via `ChunkRetryRequested` signal to the Decomposition Workflow.

### Service Map

Network A (7 services): `NetworkA.Ingestion.API`, `NetworkA.Callback.Receiver`, `NetworkA.Decomposition.Workflow`, `NetworkA.Activities.JobSetup`, `NetworkA.Activities.HeavyProcessing`, `NetworkA.Activities.Manifest`, `NetworkA.Activities.Dispatch`

Network B (5 services): `NetworkB.ProxyListener.Service`, `NetworkB.Assembly.Workflow`, `NetworkB.Activities.ManifestState`, `NetworkB.Activities.HeavyAssembly`, `NetworkB.Activities.Reporting`

All services must run simultaneously for end-to-end flows. Each connects to Temporal via `Temporal:TargetHost`.

### Shared Layer Rule

`Shared.Contracts` must have zero NuGet references — it contains only pure C# DTOs, enums, and interfaces.

## Specs & Documentation

Feature specs live in `specs/<feature-id>/` and contain `spec.md` (requirements), `quickstart.md` (startup steps), `data-model.md` (contracts/schemas), `plan.md`, and a `contracts/` subfolder with HTTP, RabbitMQ, and Temporal task queue definitions.

Architecture decision records are in `docs/` (`temporal-why-and-alternatives.md`, `horizontal-scaling.md`).
