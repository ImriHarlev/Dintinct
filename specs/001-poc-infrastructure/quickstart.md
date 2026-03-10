# Quickstart: POC Infrastructure Setup

**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- PowerShell 7+ (for Windows)

---

## Step 1: Start Infrastructure

From the repository root:

```powershell
docker-compose up -d
```

This starts:
- RabbitMQ (AMQP: `localhost:5672`, Management UI: http://localhost:15672, credentials: `guest`/`guest`)
- MongoDB (`localhost:27017`)
- Redis (`localhost:6379`)
- Temporal server (`localhost:7233`)
- Temporal Web UI: http://localhost:8233

Wait ~15 seconds for Temporal to finish auto-setup.

---

## Step 2: Build the Solution

```powershell
dotnet build Dintinct.sln
```

All 14 projects must compile without errors (SC-004).

---

## Step 3: Start Services

Open a separate terminal for each service (or use a process manager):

### Network A Services

```powershell
# Terminal 1 — Ingestion API
dotnet run --project src/NetworkA/NetworkA.Ingestion.API

# Terminal 2 — Callback Receiver
dotnet run --project src/NetworkA/NetworkA.Callback.Receiver

# Terminal 3 — Decomposition Workflow Worker
dotnet run --project src/NetworkA/NetworkA.Decomposition.Workflow

# Terminal 4 — Job Setup Activity Worker
dotnet run --project src/NetworkA/Activities/NetworkA.Activities.JobSetup

# Terminal 5 — Heavy Processing Activity Worker
dotnet run --project src/NetworkA/Activities/NetworkA.Activities.HeavyProcessing

# Terminal 6 — Manifest Activity Worker
dotnet run --project src/NetworkA/Activities/NetworkA.Activities.Manifest

# Terminal 7 — Dispatch & Retry Activity Worker
dotnet run --project src/NetworkA/Activities/NetworkA.Activities.Dispatch
```

### Network B Services

```powershell
# Terminal 8 — Proxy Event Listener
dotnet run --project src/NetworkB/NetworkB.ProxyListener.Service

# Terminal 9 — Assembly Workflow Worker
dotnet run --project src/NetworkB/NetworkB.Assembly.Workflow

# Terminal 10 — Manifest State Activity Worker
dotnet run --project src/NetworkB/Activities/NetworkB.Activities.ManifestState

# Terminal 11 — Heavy Assembly Activity Worker
dotnet run --project src/NetworkB/Activities/NetworkB.Activities.HeavyAssembly

# Terminal 12 — Reporting & Dispatch Activity Worker
dotnet run --project src/NetworkB/Activities/NetworkB.Activities.Reporting
```

**Expected startup log output** (SC-002):

```
[INF] NetworkA.Ingestion.API: MongoDB connected successfully
[INF] NetworkA.Ingestion.API: RabbitMQ connected successfully
[INF] NetworkA.Decomposition.Workflow: Temporal worker registered on queue decomposition-workflow
[INF] NetworkA.Activities.JobSetup: Redis connected successfully
[INF] NetworkA.Activities.JobSetup: MongoDB connected successfully
[INF] NetworkA.Activities.JobSetup: Temporal worker registered on queue setup-tasks
...
```

---

## Step 4: Submit a Test Ingestion Request (Happy Path)

```powershell
Invoke-RestMethod -Method POST -Uri "http://localhost:5000/api/v1/ingestion" `
  -ContentType "application/json" `
  -Body '{
    "callingSystemId": "SYS-001",
    "callingSystemName": "TestSystem",
    "sourcePath": "/network-a/data/test-package",
    "targetPath": "/network-b/output/test-package",
    "targetNetwork": "NetworkB",
    "externalId": "test-run-001",
    "answerType": "FileSystem",
    "answerLocation": "/network-a/callbacks/result.json"
  }'
```

**Expected response**:
```json
{ "jobId": "<guid>" }
```

**Observe in Temporal UI** (http://localhost:8233):
- Workflow `decomposition-<jobId>` starts and progresses through activities.
- After mock assembly completes in Network B, `assembly-<jobId>` workflow completes.
- Final status `COMPLETED` is written to `/network-a/callbacks/result.json`.

---

## Step 5: Trigger a Retry (Proxy Failure Test)

While a job is in progress, simulate a proxy failure by publishing a RabbitMQ message to the `proxy.events.queue`:

```powershell
# Via RabbitMQ Management UI (http://localhost:15672)
# Exchange: proxy.events
# Routing Key: file.arrived
# Body:
{ "filePath": "/network-b/inbox/chunk1.bin.ERROR.txt" }
```

**Expected behavior**:
1. Proxy Event Listener detects `.ERROR.txt`, sends HTTP POST to `/api/v1/callbacks/retry`.
2. Callback Receiver signals the Decomposition Workflow: `ChunkRetryRequested`.
3. Dispatch & Retry Activity re-places `chunk1.bin` in the Data Outbox.
4. Retry counter for `chunk1.bin` incremented in MongoDB (SC-003).

---

## Configuration

All configuration is in `appsettings.json` per service (no hardcoded values — FR-019, SC-006).

Key configuration keys (same across all services that use the dependency):

| Key | Description | Default |
|-----|-------------|---------|
| `MongoDB:ConnectionString` | MongoDB connection string | `mongodb://localhost:27017` |
| `MongoDB:DatabaseName` | Database name | `networkA_db` / `networkB_db` |
| `Redis:Endpoint` | Redis endpoint | `localhost:6379` |
| `RabbitMq:AmqpUri` | RabbitMQ AMQP URI | `amqp://guest:guest@localhost:5672` |
| `Temporal:TargetHost` | Temporal gRPC address | `localhost:7233` |
| `Outbox:DataOutboxPath` | Proxy Data Outbox directory | `/network-a/outbox/data` |
| `Outbox:ManifestOutboxPath` | Proxy Manifest Outbox directory | `/network-a/outbox/manifest` |
| `RetryPolicy:MaxRetryCount` | Max chunk retry attempts | `3` |
| `Mock:MockChunkCount` | Number of mock chunks to generate | `2` |
| `NetworkA:CallbackBaseUrl` | Network A Callback Receiver base URL | `http://localhost:5001` |

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Service crashes on startup | Infrastructure not ready | Wait for `docker-compose up -d` to finish, retry |
| Temporal worker not appearing in UI | Wrong `TargetHost` or namespace | Verify `Temporal:TargetHost = localhost:7233` |
| RabbitMQ consumer not receiving messages | Queue/exchange not declared | Check startup logs for topology declaration confirmation |
| MongoDB connection refused | Container not yet healthy | Wait 5–10 seconds and restart the service |
| Workflow stuck at activity step | Activity worker not running | Verify the correct Activity worker terminal is running |
