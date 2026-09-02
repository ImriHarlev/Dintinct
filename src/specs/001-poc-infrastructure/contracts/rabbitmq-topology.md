# RabbitMQ Topology Contract

**Branch**: `001-poc-infrastructure`  
**Date**: 2026-03-08

A single RabbitMQ instance serves both networks using distinct exchanges and queues (per Assumptions in spec).

---

## Exchanges

| Exchange Name | Type | Durable | Used By |
|--------------|------|---------|---------|
| `netA.ingestion` | `direct` | Yes | Ingestion API publishes ingestion requests |
| `proxy.events` | `direct` | Yes | Simulated proxy publishes file-arrival events to Network B |
| `netA.callbacks` | `direct` | Yes | Network B publishes callbacks to Network A |

---

## Queues

| Queue Name | Exchange | Routing Key | Consumer | Message Schema |
|-----------|----------|-------------|----------|----------------|
| `netA.ingestion.queue` | `netA.ingestion` | `ingest` | `NetworkA.Ingestion.API` (RabbitMQ ingestion channel) | `IngestionRequestPayload` JSON |
| `proxy.events.queue` | `proxy.events` | `file.arrived` | `NetworkB.ProxyListener.Service` | `{ "filePath": "<path>" }` |
| `netA.callbacks.queue` | `netA.callbacks` | `callback` | `NetworkA.Callback.Receiver` (optional alt to HTTP) | `StatusCallbackPayload` JSON |

---

## Message Schemas

### Proxy File Event (published by simulated proxy → consumed by Network B)

```json
{
  "filePath": "/network-b/inbox/chunk1.bin"
}
```

`Content-Type: application/json`

**JobId extraction**: the `jobId` is the substring of the filename before the first `_` (e.g., `3fa85f64` from `3fa85f64_chunk_1.bin`). All workflow interactions use `SignalWithStart` on workflow ID `assembly-{jobId}`, so the workflow is created on first contact regardless of whether the manifest or a chunk arrives first.

Routing logic based on the filename in `filePath`:

| Condition | Action |
|-----------|--------|
| ends with `.ERROR.txt` | HTTP POST to `/api/v1/callbacks/retry` on Network A (no workflow signal) |
| ends with `.UNSUPPORTED.txt` | `SignalWithStart` → `UnsupportedFileAsync` |
| ends with `_manifest.json` | `SignalWithStart` → `ManifestArrivedAsync` |
| ends with `.HARDFAIL.txt` | `SignalWithStart` → `HardFailAsync` |
| otherwise (data chunk) | `SignalWithStart` → `ChunkArrivedAsync` |

### Ingestion Request (published by external caller → consumed by Ingestion API)

`IngestionRequestPayload` serialized as JSON.  
`Content-Type: application/json`

### Status Callback (published by Network B → consumed by Network A, alternative to HTTP POST)

`StatusCallbackPayload` serialized as JSON.  
Delivery mechanism selected by `answerType` field — `RabbitMQ` means publish to `netA.callbacks.queue`.  
`Content-Type: application/json`

---

## Consumer Configuration

All consumers use:
- `autoAck: false` — explicit acknowledgement required
- `prefetchCount: 1` — back-pressure, process one message at a time per consumer
- `DispatchConsumersAsync: true` on `ConnectionFactory` (required for `AsyncEventingBasicConsumer` in RabbitMQ.Client 7.x)
- `AutomaticRecoveryEnabled: true` — reconnect on broker restart

---

## Topology Initialization

All exchanges and queues are declared idempotently at service startup (safe to call on every restart).  
Declaration is the responsibility of the service that **consumes** from the queue — the producer may also declare to prevent publish failures, but this is secondary.
