# HTTP API Contracts

**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

All HTTP endpoints are implemented as ASP.NET Core Controller actions (Constitution Principle V).  
No authentication is required in the POC (see spec Assumptions).  
All routes follow the pattern `/api/v1/[resource]`.

---

## Network A — Ingestion API (`NetworkA.Ingestion.API`)

Base URL: `http://localhost:<port>` (port configurable via environment)

### POST `/api/v1/ingestion`

Accepts a new file transfer request, validates it, persists a job record to MongoDB, and starts a Temporal Decomposition Workflow.

**Request**

```
Content-Type: application/json
```

```json
{
  "callingSystemId": "SYS-001",
  "callingSystemName": "MySystem",
  "sourcePath": "/network-a/data/package.zip",
  "targetPath": "/network-b/output/package",
  "targetNetwork": "NetworkB",
  "externalId": "ext-txn-abc123",
  "answerType": "RabbitMQ",
  "answerLocation": null
}
```

| Field | Type | Required | Validation |
|-------|------|----------|-----------|
| `callingSystemId` | `string` | Yes | Non-empty |
| `callingSystemName` | `string` | Yes | Non-empty |
| `sourcePath` | `string` | Yes | Non-empty |
| `targetPath` | `string` | Yes | Non-empty |
| `targetNetwork` | `string` | Yes | Non-empty |
| `externalId` | `string` | Yes | Non-empty |
| `answerType` | `"RabbitMQ"` \| `"FileSystem"` | Yes | Must be a valid enum value |
| `answerLocation` | `string` \| `null` | Conditional | Required when `answerType == "FileSystem"` |

**Response — 202 Accepted**

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**Response — 400 Bad Request** (validation failure)

```json
{
  "errors": {
    "sourcePath": ["'Source Path' must not be empty."],
    "answerLocation": ["'Answer Location' is required when AnswerType is FileSystem."]
  }
}
```

**Controller**: `IngestionController`  
**Route**: `[HttpPost] /api/v1/ingestion`  
**Delegates to**: `IIngestionService` → persists `Job` to MongoDB → calls `ITemporalClient.StartWorkflowAsync`

---

## Network A — Callback Receiver (`NetworkA.Callback.Receiver`)

Base URL: `http://localhost:<port>` (port configurable via environment)

### POST `/api/v1/callbacks/status`

Receives a `StatusCallbackPayload` from Network B and forwards it as a Temporal signal to the running Decomposition Workflow.

**Request**

```
Content-Type: application/json
```

```json
{
  "callingSystemId": "SYS-001",
  "callingSystemName": "MySystem",
  "sourcePath": "/network-a/data/package.zip",
  "targetPath": "/network-b/output/package",
  "targetNetwork": "NetworkB",
  "externalId": "ext-txn-abc123",
  "answerType": "RabbitMQ",
  "answerLocation": null,
  "jobId": "netb-job-789",
  "updateDate": "2026-03-08T12:00:00Z",
  "jobCount": 1,
  "origJobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "jobStatus": "COMPLETED"
}
```

**Response — 204 No Content**  
Signal accepted and forwarded to Temporal.

**Response — 400 Bad Request**  
`OrigJobId` missing or workflow not found.

**Controller**: `CallbackController`  
**Route**: `[HttpPost] /api/v1/callbacks/status`  
**Delegates to**: `ICallbackService` → looks up `TemporalWorkflowId` from MongoDB by `OrigJobId` → sends `[WorkflowSignal] FinalStatusReceivedAsync`

### POST `/api/v1/callbacks/retry`

Receives a chunk retry request from Network B (triggered when `.ERROR.txt` arrives).

**Request**

```json
{
  "origJobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "chunkName": "chunk1.bin"
}
```

**Response — 204 No Content**

**Controller**: `CallbackController`  
**Route**: `[HttpPost] /api/v1/callbacks/retry`  
**Delegates to**: `ICallbackService` → sends `[WorkflowSignal] ChunkRetryRequestedAsync(chunkName)`

---

## Network B → Network A (Outbound from Network B)

Network B's Reporting & Dispatch Activity calls these Network A endpoints. They are Network A's contracts above, called by Network B's `HttpClient`.

### POST `{NetworkACallbackBaseUrl}/api/v1/callbacks/status`
Delivers the final `StatusCallbackPayload` (SC-001 requirement).

### POST `{NetworkACallbackBaseUrl}/api/v1/callbacks/retry`
Reports proxy error for a specific chunk, requesting a retry.

---

## Notes

- No `/health` HTTP endpoint is implemented (Constitution + SC-002: startup logs are the sole observability mechanism).
- HTTPS is disabled for the POC — all services communicate over HTTP.
- Input validation uses `FluentValidation` validators registered via `AddFluentValidation` (Constitution Principle V).
- Minimal API (`app.Map*`) style is not used — all endpoints are controller actions (Constitution Principle V).
