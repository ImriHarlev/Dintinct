# Microservices Table

All services are .NET 9, containerized, deployed via OpenShift. Each NetworkA service connects only to Postgres-A and RabbitMQ-A (via PgBouncer). Each NetworkB service connects only to Postgres-B and RabbitMQ-B (via PgBouncer). The Proxy is third-party and listed here for reference only.

> **Service count: A = 9, B = 6** (incl. one `DLQ.Consumer` per network). Per-row status CAS + heartbeat is the universal dedup + liveness primitive. Next-step Rabbit publishes go through a transactional `outbox` table and a per-network **OutboxRelay** process. Diagrams: [`architecture-v3.drawio`](architecture-v3.drawio), [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio), [`chunk-flow.drawio`](chunk-flow.drawio), [`worker-exception-handling.drawio`](worker-exception-handling.drawio).
>
> Defaults: `heartbeat_interval_s=10`, `sweeper_threshold_s=30`, `max_retries=5`, `timeout_sla=30min`.
>
> Note: the 4-channel ingestion split is the user's choice; reviewers proposed one Ingestion service. See [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §7.

---

## Network A

| Service | Type | Inbound | Outbound (via `outbox` + OutboxRelay) | Postgres-A writes | Scale strategy |
|---------|------|---------|----------------------------------------|--------------------|----------------|
| **Ingestion.Api** | ASP.NET Core HTTP | `POST /api/v1/ingestion` (`IngestionRequestPayload`) | `jobs.created` | `job` insert (status=`Created`), `request_payload` JSONB; `outbox` row | HPA on RPS; small replicas (2–4). |
| **Ingestion.FolderWatcher** | Worker / HostedService | Filesystem stability poll | `jobs.created` | `job` insert; `outbox` row | 1–2 replicas (singleton per watched dir via lease). |
| **Ingestion.RabbitBridge** | Worker | External Rabbit queue carrying `IngestionRequestPayload` | `jobs.created` | `job` insert; `outbox` row | Replicas = competing consumers. |
| **Ingestion.RequestFileWatcher** | Worker | Filesystem watcher on request-file drop dir | `jobs.created` | `job` insert; `outbox` row | 1–2 replicas (lease per dir). |
| **Prepare.Worker** | Worker (CAS + heartbeat) | `jobs.created` | One `files.convert` per source file | `job.status` CAS `Created→Preparing→Prepared` (sets `worker_id`, `last_heartbeat_at`), `source_file` inserts, `nested_archive`; one `outbox` row per file. After final CAS `Preparing→Prepared`: writes `{job_id}.job.json` (job header) to manifest outbox dir via direct safe-write (staging-subdir + mv, NOT via `outbox` table). | Horizontal; CPU+IO heavy. Node selector: high-IO pool. |
| **Convert.Worker** | Worker (CAS + heartbeat) | `files.convert` | `files.split` | `source_file.status` CAS `Pending→Converting→Converted`, sets `applied_conversion` + `converted_relative_path` on `source_file` (1→1, no separate table); `outbox` row. **Permanent error:** `source_file.status='Failed'`, `failure_reason` populated. | Horizontal; CPU heavy. Node selector: CPU pool. |
| **Split.Worker** (+ inline mini-manifest write + sweepers) | Worker (CAS + heartbeat) | `files.split` | Chunk bytes to data outbox dir; per-file mini-manifest JSON to manifest outbox dir | `chunk` inserts (`source_file_id`, `ON CONFLICT DO NOTHING`), `source_file.status` CAS `Converted→Splitting→Split` (or `Failed`/`NotSupported`) — single table, no `converted_file`. Writes `{job_id}_{source_file_id}.file.json` (per-file mini-manifest) to manifest outbox dir for EVERY file (Split/Failed/NotSupported). Mini-manifest written BEFORE committing terminal DB status (heartbeat sweeper recovery guarantee). Gates `job.files_manifest_written_count` increment via `manifest_written=FALSE` CAS on `source_file`. When count reaches `total_source_files` → `job.status='AllManifestsWritten'`. `job.status` flip: `Splitting→AllManifestsWritten` (last file). Sweeper: only phase-CAS sweeper (env-var enabled); ManifestSweeper eliminated. | Horizontal; IO heavy. One replica runs phase-CAS sweeper (env-var enabled). |
| **OutboxRelay-A** | Worker | Polls `outbox WHERE published_at IS NULL` (`FOR UPDATE SKIP LOCKED`) every ~500ms | Publishes to RabbitMQ-A with publisher-confirm; updates `outbox.published_at` | `outbox.published_at` update | 2–3 replicas competing. |
| **DLQ.Consumer-A** | Worker | Binds every `*.dead` queue on RabbitMQ-A | — (terminal) | Per dead message: if `source_file.manifest_written=FALSE` → write the per-file mini-manifest (`status='Failed'`, `chunks=[]`) to the manifest outbox dir BEFORE commit (write-before-commit, §3.5), then CAS `source_file.status='Failed'`, `failure_reason`, `manifest_written=TRUE`; ack. Idempotent via `manifest_written`. | 1–2 replicas; low volume. |

Per-phase **heartbeat sweeper** (one replica with `SWEEPER_ENABLED=true`) resets rows whose `last_heartbeat_at` is older than 30s.

---

## Proxy (third-party — unchanged)

| Service | Type | Inbound | Outbound |
|---------|------|---------|----------|
| **Proxy** | Closed third-party | Watches A's data outbox + manifest outbox | Files shipped to B's mirror dirs (the **proxy delivery dir**); Rabbit message `{ filePath }` per transfer; `.ERROR.txt` / `.UNSUPPORTED.txt` markers on failure. Proxy does NOT delete files from B's delivery dir — NetworkB owns cleanup. |

---

## Network B

| Service | Type | Inbound | Outbound (via `outbox` + OutboxRelay) | Postgres-B writes / Disk writes | Scale strategy |
|---------|------|---------|----------------------------------------|--------------------------------|----------------|
| **ProxyListener** | Worker | Proxy Rabbit queue (`{filePath}`) | `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, `chunks.received`, `chunks.error`, `chunks.unsupported` — only for non-buffered rows | Seven branches (see [`chunk-flow.drawio`](chunk-flow.drawio)), classified by filename pattern: `*.job.json`→JobHeader, `*.file.json`→FileManifest, `*_chunk_*.*`→Chunk, `*.job.ERROR.txt`→JobHeaderError, `*.file.ERROR.txt`→FileManifestError, `*_chunk_*.ERROR.txt`→Error, `*.UNSUPPORTED.txt`→Unsupported. ① **Chunk:** if `expected_chunk` row exists → `INSERT inbox(kind='Chunk', published=TRUE)` + `INSERT outbox(chunks.received)`; else buffer `INSERT inbox(kind='Chunk', published=FALSE)` only. ② **JobHeader:** always `INSERT inbox(kind='JobHeader', published=TRUE)` + `INSERT outbox(job-headers.received)`. ③ **FileManifest:** if job row exists → `INSERT inbox(kind='FileManifest', published=TRUE)` + `INSERT outbox(file-manifests.received)`; else buffer `INSERT inbox(kind='FileManifest', published=FALSE)` only. ④ **FileManifestError:** `INSERT inbox(kind='FileManifestError', published=TRUE)` + `INSERT outbox(file-manifest-errors.received)`. ⑤ **JobHeaderError:** `INSERT inbox(kind='JobHeaderError', published=TRUE)` only — audit, no outbox (cannot recover without metadata). ⑥ **Error / Unsupported:** existing chunk error/unsupported handling. All inserts `ON CONFLICT (file_path) DO NOTHING`. **No file copy at any branch** — chunks stay in proxy delivery dir. | Horizontal; competing consumers. Synchronous two-stat stability check. |
| **Assembly.Worker** | Worker (CAS + heartbeat) | `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, `chunks.received` | `files.assembled`, `files.finalized` (error path) | Three consumers: **① `job-headers.received`:** create `job` row (`expected_file_count` from header, `status='Awaiting'`, `job_header_received_at=now()`); drain all buffered `FileManifest` inbox rows for this job (for each: create `expected_file`+`expected_chunk`); drain all buffered chunks for each newly-created expected_file; run concat for any complete files; flip `Awaiting→Assembling` if first file processed. NOTE: tx can be large if job header was delayed (3-level drain — risk accepted, comment in code). **② `file-manifests.received`:** create `expected_file`+`expected_chunk` rows (`ON CONFLICT DO NOTHING`); inline drain buffered chunks for this file; concat if complete; `INSERT outbox(files.assembled)`; flip `Awaiting→Assembling` if first file. **③ `file-manifest-errors.received`:** create `expected_file(status=Failed, expected_chunk_count=0, failure_reason='ProxyDeliveryFailure')`; `INSERT outbox(files.finalized, status=Failed)`. **Chunk path (unchanged):** CAS `expected_chunk.received_at`, increment `expected_file.received_chunk_count`; if flips to `Assembled`, concat + `INSERT outbox(files.assembled)` before COMMIT. | Horizontal. No advisory locks, no NACK+requeue dance for early chunks. |
| **ReverseConverter.Worker** | Worker (CAS + heartbeat) | `files.assembled` | `files.finalized`; final file under `target_path` via `.tmp+rename` | Reads `work/{job_id}/assembled/{file_id}.bin`. CAS `expected_file.status Assembled→ReverseConverting→Finalized`; `outbox(files.finalized)`. Never touches proxy delivery dir or per-chunk bytes. | Horizontal; CPU heavy. Node selector: CPU pool. |
| **Reporter.Worker** + sweepers on one replica | Worker (CAS + heartbeat) | `files.finalized`, `chunks.error`, `chunks.unsupported`; cron `ReportPendingSweeper`, `TimeoutSweeper`, phase-CAS sweeper | CSV to `target_path` (`.tmp+rename`); `StatusCallbackPayload` via `IAnswerDispatcher` (FileSystem JSON or external Rabbit) | `expected_file.status` CAS to terminal (`Finalized`/`Failed`/`NotSupported`), `job.finalized_file_count` increment, `job.status → ReportPending → Done`; `job.callback_sent_at` CAS guards the terminal status flip; callback **delivery is at-least-once** (dispatch precedes the commit recording it — a crash in between re-dispatches on sweeper retry), so the consuming system must be idempotent on `jobId`/`externalId` (see §5.6 + QUESTIONS §3). On terminal job: deletes `work/{job_id}/`, chunk + manifest files from proxy delivery dir; sets `job.cleaned_at`. | Small replicas (2–4); one replica runs sweepers. |
| **OutboxRelay-B** | Worker | Polls `outbox WHERE published_at IS NULL` every ~500ms | Publishes to RabbitMQ-B with publisher-confirm; updates `outbox.published_at` | `outbox.published_at` update | 2–3 replicas competing via `FOR UPDATE SKIP LOCKED`. |
| **DLQ.Consumer-B** | Worker | Binds every `*.dead` queue on RabbitMQ-B | — (terminal) | Per dead message: CAS `expected_file.status='Failed'`, `failure_reason`; ack. No manifest write on B (no `manifest_written` column — Reporter learns from `expected_file.status`). Job continues; job-level terminal resolved by Reporter / TimeoutSweeper. | 1–2 replicas; low volume. |

**Job-level `Failed` is set ONLY by:**
1. Reporter (all `expected_file` rows `Failed`/`NotSupported`).
2. `TimeoutSweeper` (30-min SLA exceeded → `TimedOut`).
3. Reporter resolving `JobStatus='Timeout'` when manifest never arrived.

Workers never set `job.status='Failed'` directly. See [`worker-exception-handling.drawio`](worker-exception-handling.drawio).

---

## Shared Infrastructure (per network)

| Component | Role |
|-----------|------|
| **RabbitMQ** | Quorum queues for all internal traffic. One `*.retry` queue per main queue (1-min TTL, `x-retry-count` header), terminal `*.dead` queue. `max_retries=5` before DLQ. Backpressure via `x-max-length` + `x-overflow=reject-publish` on internal queues so OutboxRelay sees nacks; `reject-publish-dlx` only on terminal-edge queues. Topology immutable args in `definitions.json`; tunables (max-length, TTL) as **policies**. Plain direct/topic exchanges — no consistent-hash routing. |
| **PostgreSQL** | One cluster per network. Tables in [ARCHITECTURE.md §3.2 and §5.2](ARCHITECTURE.md). Includes `outbox` and `phase_config` tables. No partitioning day-1. Migrations via OpenShift `Job` (Helm pre-upgrade hook). Year-2: monthly partitioning on `chunk`, `expected_chunk`, `outbox`. |
| **PgBouncer** | Transaction-pooling mode (single pool) in front of each Postgres cluster. |
| **Storage** | Local PV (RWX or RWO) for A and B work dirs. Outbox/target dirs are filesystem. **Proxy delivery dir on B is an RWX PV shared by ProxyListener (writes via proxy) + Assembly (reads in-place during concat).** S3-compatible storage opt-in per volume via `IStorage`. |
| **Sealed Secrets / external-secrets-operator** | Connection strings, dispatcher credentials. |
| **`phase_config` table** | Holds `heartbeat_interval_s` (default 10), `sweeper_threshold_s` (default 30 = 3× interval), `max_retries` (default 5) per phase. Per-phase overrides only if measured timing demands them. |

---

## Service-to-Queue Mapping (quick reference)

### NetworkA queues

| Queue | Producers | Consumers |
|-------|-----------|-----------|
| `jobs.created` | Ingestion.* (via outbox + relay) | Prepare.Worker |
| `files.convert` | Prepare.Worker (via outbox + relay) | Convert.Worker |
| `files.split` | Convert.Worker (via outbox + relay) | Split.Worker |

(`files.split.done` removed — Split.Worker handles the per-source-file finalization itself.)

### NetworkB queues

| Queue | Producers | Consumers |
|-------|-----------|-----------|
| `job-headers.received` | ProxyListener (via outbox + relay) | Assembly.Worker |
| `file-manifests.received` | ProxyListener (via outbox + relay) | Assembly.Worker |
| `file-manifest-errors.received` | ProxyListener (via outbox + relay) | Assembly.Worker |
| `chunks.received` | ProxyListener (via outbox + relay) | Assembly.Worker |
| `chunks.error` | ProxyListener (via outbox + relay) | Reporter.Worker |
| `chunks.unsupported` | ProxyListener (via outbox + relay) | Reporter.Worker |
| `files.assembled` | Assembly.Worker (via outbox + relay) | ReverseConverter.Worker |
| `files.finalized` | ReverseConverter.Worker (via outbox + relay) | Reporter.Worker |

Each main queue has one `*.retry` (1-min TTL) and one `*.dead` companion. **Chunks arriving before their file-manifest** are NOT NACK+requeue'd — they are durably **buffered** in `inbox` (`published=FALSE`); Assembly's file-manifest handler drains them inline in a single transaction. **File-manifests arriving before their job header** are similarly buffered (`published=FALSE`) and drained when the job header arrives. See [`chunk-flow.drawio`](chunk-flow.drawio).

---

## Heartbeat / Sweeper Mechanics

Every worker that holds a row in a non-terminal in-progress state runs a **HeartbeatLoop** alongside the work:

```
every 10s:
  UPDATE entity SET last_heartbeat_at = now() WHERE id = $row_id AND worker_id = $me
```

Per-phase sweeper (one replica with `SWEEPER_ENABLED=true`, runs every 60s):

```sql
UPDATE entity
   SET status='<previous>', worker_id=NULL, last_heartbeat_at=NULL
 WHERE status='<in-progress>'
   AND last_heartbeat_at < now() - interval '30 seconds'
 RETURNING id;
-- for each id: re-insert outbox row to republish from the previous queue
```

Worst-case sweeper-recovery latency = 30s threshold + 60s poll = ~90s. Outbox closes the post-COMMIT-pre-publish window to ~500ms.

See [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) diagrams 1, 2, 3.

---

## Exception Classification (per [`worker-exception-handling.drawio`](worker-exception-handling.drawio))

| Class | Examples | Action |
|-------|----------|--------|
| **Transient** | `SqlException` (transient), `BrokerUnreachableException`, `IOException` (network), `TimeoutException`, `OperationCanceledException` (non-shutdown) | `nack(requeue=false)` → `*.retry` (1-min TTL) → redeliver up to 5× |
| **Permanent** | `UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException` | `UPDATE source_file/expected_file SET status='Failed', failure_reason=…` → `nack(requeue=false)` → `*.dead`. Job continues for other files. |
| **Unknown** | Anything else | Transient by default. DLQ catches loops after 5 retries. |
