# Dintinct — New Architecture (v3, Temporal-free)

> Cross-network file processing pipeline: **Network A** decomposes packages → **Proxy** (closed, third-party) ships files → **Network B** assembles. Designed for ~1M jobs/month, horizontal scale, crash resilience, no orchestrator framework.
>
> This document is aligned with the following diagrams and is authoritative together with them:
> - [`architecture-v3.drawio`](architecture-v3.drawio) — service topology
> - [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) — normal flow, crash recovery (sweeper), post-COMMIT crash (outbox)
> - [`chunk-flow.drawio`](chunk-flow.drawio) — 5-branch ProxyListener flow: chunk/job-header/file-manifest buffering, inline drains, ERROR sentinels
> - [`worker-exception-handling.drawio`](worker-exception-handling.drawio) — transient vs permanent failure paths

---

## 1. Guiding Principles

1. **No orchestration framework.** Replace Temporal with explicit, idempotent step-services coordinated by RabbitMQ + PostgreSQL.
2. **Per-network isolation.** Each side has its own Postgres and RabbitMQ; A and B never share infra or talk directly. The Proxy is the only bridge — over filesystem (data + manifest dirs) and the proxy's RabbitMQ message.
3. **Crash-safe via per-row status CAS + heartbeat sweeper.** Idempotency rests on one primitive: every state transition is `UPDATE row SET status='Next', worker_id=$me, last_heartbeat_at=now() WHERE id=$1 AND status='Prev' RETURNING id` — only the winner does work; redeliveries are no-ops. A heartbeat loop refreshes `last_heartbeat_at` every 10s; a per-phase sweeper resets rows whose heartbeat is older than 30s (worker died) and re-publishes them.
4. **Transactional outbox for next-step publishes — both A and B.** The DB commit and the downstream Rabbit publish are decoupled via an `outbox` table. The worker inserts the next-step message into `outbox` in the same transaction as the state flip; a separate `OutboxRelay` polls and publishes with publisher-confirm, marking `published_at=now()`. This closes the "COMMIT succeeded, crash before publish" gap.
5. **Per-file batch on NetworkB, chunks read in-place.** Each file finalizes the moment all its chunks are present — no global package wait. Chunks arriving before their manifest are buffered in `inbox` with `published=FALSE`; when the manifest arrives, Assembly drains the buffered rows inline. **Chunk bytes are NEVER copied** — Assembly reads chunks directly from the proxy delivery dir during concat.
6. **Defer scale machinery until metrics demand it.** No partitioning, no consistent-hash routing, no leader-elected sweepers on day one.
7. **Business logic preserved, plumbing replaced.** Splitters, converters, assemblers, reverse-converters remain. What changes is *how* they are dispatched and tracked.
8. **Boundaries are immutable.** Proxy rabbit message, `StatusCallbackPayload`, final CSV format, and `IngestionRequestPayload` are contracts and do not change.

---

## 2. High-level Diagram

See [`architecture-v3.drawio`](architecture-v3.drawio).

```
                          NETWORK A                                       PROXY                                NETWORK B
 ┌──────────────────────────────────────────────────────┐    ┌───────────────────────┐    ┌──────────────────────────────────────────────────────────┐
 │  Ingestion API ─┐                                    │    │  Third-party,         │    │  Proxy Listener (Rabbit consumer)                        │
 │  Folder Watcher ┤                                    │    │  closed system.       │    │     │                                                    │
 │  Rabbit Bridge  ├─► [jobs.created] ──► Prepare ──────┼──► │  Watches              │    │     ├─► [job-headers.received]     ─► Assembly           │
 │  ReqFile Watcher┘                        │  └─job hdr │    │  data outbox  ──►     │    │     ├─► [file-manifests.received] ─► Assembly           │
 │                                          ▼            │    │  manifest outbox      │    │     ├─► [file-manifest-errors.received] ─► Assembly      │
 │                                        Convert        │    │                       │    │     ├─► [chunks.received]         ─► Assembly           │
 │                                          │            │    │  ships each file      │    │     ├─► [chunks.error]            ─► Reporter           │
 │                                          ▼            │    │  to Network B and     │    │     └─► [chunks.unsupported]      ─► Reporter           │
 │                                        Split ──► chunks    │  publishes Rabbit     │    │           │                                            │
 │                                          │  └─ per-file    │  with filePath        │    │           ▼                                            │
 │                                          │     manifest ──►│                       │    │     Assembly (concat) ─► [files.assembled] ─► RevConv   │
 │                                          │                 │                       │    │                                              │         │
 │  Postgres-A  +  RabbitMQ-A  +  OutboxRelay-A              │                       │    │                                              ▼         │
 │                                                  │         │                       │    │                                         [files.finalized] │
 │                                                  │         │                       │    │                                              │         │
 └──────────────────────────────────────────────────┘         └───────────────────────┘    │                                              ▼         │
                                                                                           │                                         Reporter (CSV + Cb) │
                                                                                           │  Postgres-B  +  RabbitMQ-B  +  OutboxRelay-B             │
                                                                                           └──────────────────────────────────────────────────────────┘
```

Key flow changes vs. v2: **Prepare** writes a `{job_id}.job.json` job-header to the manifest outbox dir immediately after completing (no longer waits for Split). **Split** writes a `{job_id}_{source_file_id}.file.json` per-file mini-manifest to the manifest outbox dir after each file is split (or permanently fails). The single large end-of-job manifest is eliminated.

---

## 3. Network A — Decomposition Side

### 3.1 Services

| # | Service | Responsibility |
|---|---------|----------------|
| 1 | **Ingestion.Api** | HTTP `POST /api/v1/ingestion`. Validates `IngestionRequestPayload`, inserts `job` row (status=`Created`), inserts `outbox(queue='jobs.created', payload)`. Returns `jobId`. |
| 2 | **Ingestion.FolderWatcher** | Polls watched directories; on stable file, builds payload, same insert+outbox as API. |
| 3 | **Ingestion.RabbitBridge** | Consumes external Rabbit queue carrying `IngestionRequestPayload`; same insert+outbox as API. |
| 4 | **Ingestion.RequestFileWatcher** | Watches request-file drop folder; reads JSON, same insert+outbox as API. |
| 5 | **Prepare.Worker** | Consumes `jobs.created`. Claims via CAS (`Created→Preparing`). Extracts archives recursively, lists files, records `source_file` rows per file, inserts one `outbox(queue='files.convert', payload)` per file. After final CAS `Preparing→Prepared`, writes `{job_id}.job.json` (job header) to the manifest outbox dir via safe-write protocol. The job header carries all metadata NB needs to create a job row before any file-manifests arrive (see §3.6). |
| 6 | **Convert.Worker** | Consumes `files.convert`. Claims via CAS (`source_file.status Pending→Converting`). Runs `IFileConverter` (1→1), sets `applied_conversion` + `converted_relative_path` on the `source_file`, final CAS `Converting→Converted`, inserts one `outbox(queue='files.split', payload)`. |
| 7 | **Split.Worker** | Consumes `files.split`. Claims via CAS (`Pending→Splitting`). Splits per proxy rules, records `chunk` rows, writes chunk bytes to data outbox dir. After per-converted-file completion CAS, writes `{job_id}_{source_file_id}.file.json` (per-file mini-manifest) to manifest outbox dir via safe-write protocol — one per source file, written immediately after that file completes (or on permanent failure, written BEFORE committing terminal status to DB). Updates `source_file.manifest_written` flag and `job.files_manifest_written_count` counter via gated CAS; when counter reaches `total_source_files`, flips `job.status='AllManifestsWritten'`. No single end-of-job manifest; no ManifestSweeper. |
| 8 | **OutboxRelay-A** | Polls `outbox WHERE published_at IS NULL` every ~500ms (`FOR UPDATE SKIP LOCKED`), publishes to RabbitMQ with publisher-confirm, updates `outbox.published_at`. |

> Per-file mini-manifest write is folded into Split on the CAS-winning worker for each source file. The `manifest_written` flag on `source_file` prevents double-counting across retries (sweeper resets status/worker_id/last_heartbeat_at but NOT `manifest_written`).
>
> **Open trade-off on ingestion services:** the user chose 4 separate ingestion services (one per channel) for crash isolation. Reviewers argued for a single `Ingestion` service hosting all 4 channels as in-process adapters. Recorded in [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §7.

### 3.2 Postgres-A Schema (minimal)

```
job (
  id UUID PK,
  external_id TEXT UNIQUE,
  request_payload JSONB,
  source_path TEXT,
  target_path TEXT,
  target_network TEXT,
  calling_system_id TEXT,
  calling_system_name TEXT,
  answer_type TEXT,
  answer_location TEXT,
  package_type TEXT,
  original_package_name TEXT,
  total_source_files INT,
  files_manifest_written_count INT DEFAULT 0,
  status TEXT,                      -- Created | Preparing | Prepared | Splitting | AllManifestsWritten | Failed
  worker_id TEXT NULL,
  last_heartbeat_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ
);
CREATE INDEX job_status_active ON job(status) WHERE status NOT IN ('AllManifestsWritten','Failed');
CREATE INDEX job_stale_hb ON job(last_heartbeat_at) WHERE last_heartbeat_at IS NOT NULL;

source_file (
  id UUID PK,
  job_id UUID FK,
  original_relative_path TEXT,
  original_format TEXT,
  applied_conversion TEXT NULL,
  converted_relative_path TEXT NULL,   -- output of the 1→1 converter; = original_relative_path for pass-through (Q11: converter is always 1→1)
  status TEXT,                      -- Pending | Converting | Converted | Splitting | Split | Failed | NotSupported
  failure_reason TEXT NULL,         -- populated when status terminal-Failed/NotSupported; carried into per-file manifest
  manifest_written BOOLEAN NOT NULL DEFAULT FALSE,  -- set after mini-manifest written; never reset by sweeper
  worker_id TEXT NULL,
  last_heartbeat_at TIMESTAMPTZ NULL,
  UNIQUE (job_id, original_relative_path)
);
-- converted_file table removed: converter is 1→1 (Q11), so conversion output folds into source_file
--   (applied_conversion + converted_relative_path). Convert and Split both CAS on source_file.

chunk (
  id UUID PK,
  job_id UUID,
  source_file_id UUID,
  index INT,
  name TEXT,
  byte_length BIGINT,
  written_at TIMESTAMPTZ,
  UNIQUE (job_id, source_file_id, index)
);
CREATE INDEX chunk_job ON chunk(job_id);

nested_archive (
  id UUID PK,
  job_id UUID FK,
  archive_relative_path TEXT,
  parent_archive_id UUID NULL FK
);

outbox (
  id BIGSERIAL PK,
  queue TEXT NOT NULL,
  payload JSONB NOT NULL,
  published_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX outbox_pending ON outbox(created_at) WHERE published_at IS NULL;

phase_config (
  phase TEXT PRIMARY KEY,            -- 'Prepare' | 'Convert' | 'Split' | 'Assembly' | 'ReverseConvert' | 'Report' | 'ProxyListener'
  heartbeat_interval_s INT NOT NULL DEFAULT 10,
  sweeper_threshold_s INT NOT NULL DEFAULT 30,   -- 3 × heartbeat_interval
  max_retries INT NOT NULL DEFAULT 5
);
```

Defaults (`heartbeat_interval_s=10`, `sweeper_threshold_s=30`, `max_retries=5`) apply to every phase. Values are tunable per phase via `phase_config` if a specific phase needs different timing.

### 3.3 Step contract (applies to every A-worker and every B-worker)

This is the canonical worker loop. Diagram 1 of [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) is the visual reference.

```
on inbound message:
  BEGIN tx
    short-circuit: if job.status terminal (Failed/Done/TimedOut) → ack + return
    CAS the row:
      UPDATE entity
         SET status='Next', worker_id=$me, last_heartbeat_at=now()
       WHERE id=$1 AND status='Prev'
       RETURNING id
    IF CAS returned no row → ack + return (already processed by someone else)
    start HeartbeatLoop (background task; UPDATE last_heartbeat_at=now() every 10s)
    do business work (write deterministically-named files; INSERT child rows ON CONFLICT DO NOTHING)
    final CAS on entity to next state (status='Done-for-this-step')
    INSERT INTO outbox(queue, payload, published_at=NULL) for the next-step message(s)
  COMMIT tx
  stop HeartbeatLoop
  ack inbound
```

**OutboxRelay** (one process pair per network, 2–3 replicas competing) runs continuously:

```
loop every ~500ms:
  SELECT id, queue, payload
    FROM outbox
    WHERE published_at IS NULL
    ORDER BY id
    FOR UPDATE SKIP LOCKED
    LIMIT 100
  for each row:
    rabbit.publish(queue, payload, confirm=sync)
    UPDATE outbox SET published_at=now() WHERE id=$1
```

**Crash scenarios:**

- Before COMMIT → tx rollback. Inbound redelivers; new worker tries CAS; succeeds (state still `Prev`) and retries from scratch.
- After COMMIT, before ack → inbound redelivers; new worker's CAS sees `status='Next'` → ack+exit. Outbox row already exists → OutboxRelay publishes it.
- Worker hung mid-step (no rollback, no crash) → row stuck in `Next` state with stale `last_heartbeat_at` → sweeper resets it after 30s + sweeper-poll-delay.

**Sweeper (per phase, runs every 60s, one replica with `SWEEPER_ENABLED=true`):**

```sql
UPDATE entity
   SET status='Prev',                       -- reset to upstream state
       worker_id=NULL,
       last_heartbeat_at=NULL
 WHERE status='Next'                        -- the "in-progress" state for this phase
   AND last_heartbeat_at < now() - interval '30 seconds'
 RETURNING id;
-- for each returned id: insert outbox row to republish from the previous step's queue
```

Diagram 2 of [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) walks the full sequence. Worst-case recovery latency ≈ `sweeper_threshold_s + sweeper_interval_s` = 30 + 60 = 90s.

**Why both heartbeat-sweeper AND outbox:**
- Heartbeat-sweeper covers "crash before COMMIT" / "worker hung mid-step" → row reset, message republished. Recovery ~90s.
- Outbox covers "COMMIT succeeded, crash before downstream publish" → relay republishes from durable outbox row. Recovery ~500ms.

**Publisher confirms are non-negotiable inside the OutboxRelay.** Without sync confirms, `published_at=now()` could be set on a message Rabbit dropped.

### 3.4 Decomposition flow (precise)

1. **Ingest** → insert `job(status=Created)` + `outbox(queue='jobs.created', payload={job_id})`. COMMIT, return jobId.
2. **Prepare** consumes `jobs.created`. CAS `job.status Created→Preparing` (sets worker_id, last_heartbeat_at). Extracts archives into work dir. For every leaf file: insert `source_file(status='Pending')`. Sets `total_source_files`. Final CAS `job.status Preparing→Prepared`. Inserts one `outbox(queue='files.convert')` per file. **After COMMIT**, writes `{job_id}.job.json` to the **manifest outbox dir** via safe-write protocol. This job-header file lets NB create a `job` row and begin accepting file-manifests before any Split.Worker finishes.
3. **Convert** consumes each `files.convert`. CAS `source_file.status Pending→Converting`. Looks up `proxy_rule`. Runs converter (1→1); sets `applied_conversion` + `converted_relative_path` on the `source_file`. Final CAS `source_file.status Converting→Converted`. Inserts one `outbox(queue='files.split')`.

   > **Determinism requirement:** the converter must be deterministic.

4. **Split** consumes each `files.split`. CAS `source_file.status Converted→Splitting`. Splits the converted file into chunks named `{job_id}_{source_file_id}_chunk_{index}{ext}` (unique because the converter is 1→1 — one converted file per source file, so `index` never collides). Writes bytes to **data outbox dir** via the safe-write protocol in §4.2. Inserts `chunk` rows (`source_file_id`). The terminal `Splitting→Split` flip is NOT done here — it happens in step 5 after the mini-manifest is written, so the flip and the `manifest_written` gate commit together.

5. **Split, same handler** — per-source-file completion + per-file mini-manifest:

   With the `source_file` still in `Splitting`, the worker writes the mini-manifest first, then commits the terminal `Split` flip:

   a. **Write mini-manifest first (crash-safety sequencing):** build `{job_id}_{source_file_id}.file.json` (deterministic JSON — see §3.6) and write it to the **manifest outbox dir** via safe-write protocol. This happens BEFORE the DB commit that sets terminal status. Rationale: if the worker crashes after writing the file but before committing, the sweeper resets `source_file.status` to `Pending`; the retried worker re-splits (idempotent chunk names), re-writes the identical mini-manifest, and then commits — resulting in the same final state. The file write is therefore safe to redo.

   b. **Gate counter increment with `manifest_written` flag:**

   ```sql
   -- Only increment if this is the first time manifest was written for this file.
   UPDATE source_file
      SET status = 'Split',
          manifest_written = TRUE
    WHERE id = $source_file_id
      AND manifest_written = FALSE
    RETURNING id;
   -- (only executes the job counter update if RETURNING produced a row)

   UPDATE job
      SET files_manifest_written_count = files_manifest_written_count + 1,
          status = CASE
                     WHEN files_manifest_written_count + 1 = total_source_files
                     THEN 'AllManifestsWritten'
                     ELSE status
                   END,
          updated_at = now()
    WHERE id = $job_id
    RETURNING files_manifest_written_count, total_source_files, status;
   ```

   COMMIT tx.

   The `manifest_written = FALSE` guard ensures that if the worker retries after the file was written but before the original DB commit, the counter is incremented exactly once per source file. **The heartbeat sweeper resets `status`, `worker_id`, and `last_heartbeat_at` but NEVER resets `manifest_written`.**

   When `files_manifest_written_count` reaches `total_source_files`, `job.status` flips to `AllManifestsWritten`. No ManifestSweeper is required.

   > **Permanent failure path:** on a permanent failure for a source file (either in this handler or via the DLQ consumer), the same write-before-commit sequencing applies: write the mini-manifest (with `status='Failed'`, `chunks=[]`) to the manifest outbox dir BEFORE committing `source_file.status='Failed'` to the DB. The `manifest_written` gate still guards the counter increment.

### 3.5 Retry policy (NetworkA) — aligned with `worker-exception-handling.drawio`

On worker exception, the worker classifies the exception against a fixed whitelist:

| Class | Example exception types | Action |
|-------|--------------------------|--------|
| **Transient** | `SqlException` (transient subclass), `BrokerUnreachableException`, `IOException` (network-related), `TimeoutException`, `OperationCanceledException` (non-shutdown), Polly transient handlers | `nack(requeue=false)` → DLX → `*.retry` queue (1-min TTL) → redeliver with `x-retry-count++` |
| **Permanent** | `UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException`, deterministic application exceptions | `UPDATE source_file SET status='Failed', failure_reason=<message>` (A-side analog of B-side `expected_file`); `nack(requeue=false)` → `*.dead` |
| **Unknown** | Anything not in either list | Treat as **transient** by default. Retries cover spurious errors; if the loop persists, DLQ catches it after `max_retries`. |

- **`max_retries` default = 5.** After `x-retry-count >= 5`, the transient message lands in `*.dead`. **Both** kinds of dead-lettered message reach the same `*.dead` DLQ consumer: (a) transient-exhausted messages, and (b) permanent-failure messages `nack`ed straight to `*.dead` (table row above). The consumer is idempotent on `source_file.manifest_written`: if `manifest_written=FALSE` it writes the per-file mini-manifest (with `status='Failed'`, `chunks=[]`) to the manifest outbox dir **before** committing the terminal status, then flips `source_file.status='Failed'` and sets `manifest_written=TRUE`; if `manifest_written=TRUE` (permanent path already wrote it inline per §3.4 step 5) the manifest write is skipped and only the status flip is committed. Both paths therefore follow the same write-before-commit sequencing, so Network B always learns the file failed and never waits on it forever.
- **Job-level `Failed` is set ONLY by:** (i) Split's per-file counter reaching `total_source_files` when all source files are `Failed`/`NotSupported`, or (ii) external operator intervention. Workers never set `job.status='Failed'` directly mid-pipeline.
- **Backpressure:** every internal main queue declared with `x-max-length` + `x-overflow=reject-publish` (NOT `-dlx`). When OutboxRelay's publish gets `basic.nack`, it leaves the outbox row unpublished and retries next poll. Stall propagates upstream as outbox tables grow (visible in metrics) and ultimately as `basic.publish` nacks at Ingestion.Api (HTTP 429). `reject-publish-dlx` is used only on terminal-edge queues.
- Tunable values (`x-max-length`, `x-message-ttl`) set via **policies**, not declaration args.

### 3.6 Per-file manifest schema

There are two distinct file types written to the manifest outbox dir:

**Job header** — `{job_id}.job.json` — written by Prepare.Worker after `Preparing→Prepared`:

```jsonc
{
  "jobId": "...",
  "totalSourceFiles": 42,
  "targetPath": "...",
  "callingSystemId": "...",
  "callingSystemName": "...",
  "externalId": "...",
  "answerType": "...",
  "answerLocation": "...",
  "packageType": "...",
  "originalPackageName": "...",
  "sourcePath": "..."
}
```

NB's Assembly.Worker consuming `job-headers.received` uses this to INSERT the `job` row with `expected_file_count = totalSourceFiles` and `status='Awaiting'`.

**Per-file mini-manifest** — `{job_id}_{source_file_id}.file.json` — written by Split.Worker after each source file completes (or permanently fails):

```jsonc
{
  "jobId": "...",
  "sourceFileId": "...",
  "originalRelativePath": "...",
  "originalFormat": "...",
  "appliedConversion": "...",          // null if no conversion
  "status": "Split",                   // Split | Failed | NotSupported
  "failureReason": null,               // populated for Failed / NotSupported
  "chunks": [
    { "index": 0, "name": "...", "byteLength": 12345 }
  ]
}
```

For failed files: `status` is `"Failed"` or `"NotSupported"`, `failureReason` is set, and `chunks` is `[]`.

NB's Assembly.Worker consuming `file-manifests.received` uses each mini-manifest to upsert one `expected_file` + its `expected_chunk` rows. If `status` is `Failed` or `NotSupported`, it creates `expected_file` in a terminal state with no `expected_chunk` rows — NB never waits for chunks that will never arrive. Reporter's terminal-count logic then sees those files as already terminal.

---

## 4. The Proxy

The Proxy is **third-party and unchanged**. It:

1. Watches the data outbox dir and manifest outbox dir on the A side.
2. Ships each file unmodified to corresponding dirs on the B side (referred to throughout this document as the **proxy delivery dir**).
3. On failure, drops a sentinel: `<original-name>.ERROR.txt` or `<original-name>.UNSUPPORTED.txt`.
4. Publishes a Rabbit message per transferred file:

   ```json
   { "filePath": "<absolute-path-on-B-side>" }
   ```

**This contract is frozen.** The proxy does NOT delete files from the proxy delivery dir on Network B — Network B owns those files from the moment they land there. Cleanup is Network B's responsibility (see §6.6).

### 4.1 Open question — partial-file pickup

NetworkA must NOT write `.tmp`-named files to the proxy outbox. Recommended: write to a staging subdirectory inside the same filesystem (which the proxy does NOT watch), then a single `mv` to the watched directory using the final filename. See [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §1.

### 4.2 Safe-write protocol

- Local writes (B's working files, target_path, NetworkB CSV) — `.tmp + rename` on the same filesystem.
- Outbox writes (A → proxy) — staging-subdir + rename to final filename. Configurable per outbox path.

---

## 5. Network B — Assembly Side

### 5.1 Services

| # | Service | Responsibility |
|---|---------|----------------|
| 1 | **ProxyListener** | Consumes proxy Rabbit. Two-stat synchronous stability check (§5.9). For each event, inserts `inbox` row + (conditionally) `outbox` row in a single transaction. Seven branches classified by filename pattern — see §5.3 and [`chunk-flow.drawio`](chunk-flow.drawio). Handles: job headers (`{job_id}.job.json`), per-file mini-manifests (`{job_id}_{source_file_id}.file.json`), chunks, and ERROR/UNSUPPORTED sentinels. **No file copy at any point** — chunks stay in the proxy delivery dir until Assembly reads them during concat. |
| 2 | **Assembly.Worker** | Consumes `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, and `chunks.received`. **Job-header path:** INSERT `job` row (`status='Awaiting'`, `expected_file_count`), drain buffered file-manifest and chunk inbox rows inline. **File-manifest path:** upsert one `expected_file` (terminal-Failed for failed files; Pending otherwise) + its `expected_chunk` rows, drain buffered chunks for this file inline, run concat if complete. **File-manifest-error path:** create terminal `expected_file` with `status='Failed'`, emit `outbox(files.finalized)`. **Chunk path:** CAS `expected_chunk.received_at`, increment `expected_file.received_chunk_count`; if status flips to `Assembled`, run concat before COMMIT, then `outbox(files.assembled)`. |
| 3 | **ReverseConverter.Worker** | Consumes `files.assembled`. Reads the assembled file from `work/{job_id}/assembled/{file_id}.bin`, applies reverse `IFileConverter` (or pass-through), writes the result under `target_path` via `.tmp + rename`. CAS `expected_file.status Assembled→ReverseConverting→Finalized`. Writes `outbox(files.finalized)`. Pure transform — never touches the proxy delivery dir or per-chunk bytes. |
| 4 | **Reporter.Worker** | Consumes `files.finalized`, `chunks.error`, `chunks.unsupported`. CAS `expected_file.status` to terminal; increments `job.finalized_file_count`; on completion flips `job.status='ReportPending'`, writes `{job_id}_report.csv`, dispatches `StatusCallbackPayload` via `IAnswerDispatcher`, final CAS to `Done`. Hosts `ReportPendingSweeper`, `TimeoutSweeper`, and per-phase heartbeat sweepers on a single sweeper-enabled replica. Also owns proxy-delivery-dir cleanup on terminal job (see §6.6). |
| 5 | **OutboxRelay-B** | Polls `outbox WHERE published_at IS NULL` every ~500ms, publishes to RabbitMQ with publisher-confirm, marks `published_at=now()`. 2–3 replicas competing via `FOR UPDATE SKIP LOCKED`. |

### 5.2 Postgres-B Schema (minimal)

```
inbox (
  file_path TEXT PRIMARY KEY,        -- proxy file paths are unique
  received_at TIMESTAMPTZ DEFAULT now(),
  kind TEXT,                         -- JobHeader | FileManifest | Chunk | JobHeaderError | FileManifestError | Error | Unsupported
  job_id UUID NULL,                  -- parsed from filename; NULL only if filename is unparseable
  source_file_id UUID NULL,          -- set for FileManifest / FileManifestError rows; NULL for JobHeader / Chunk
  published BOOLEAN DEFAULT FALSE    -- TRUE once a downstream queue message has been emitted (or the inline drain has consumed this row)
);
CREATE INDEX inbox_unpublished_by_job ON inbox(job_id) WHERE published = FALSE;

job (
  id UUID PRIMARY KEY,
  source_path TEXT,
  target_path TEXT,
  target_network TEXT,
  calling_system_id TEXT,
  calling_system_name TEXT,
  external_id TEXT,
  answer_type TEXT,
  answer_location TEXT,
  package_type TEXT,
  original_package_name TEXT,
  job_header_received_at TIMESTAMPTZ NULL,   -- set when job-header processed; replaces manifest_received_at
  expected_file_count INT NULL,
  finalized_file_count INT DEFAULT 0,
  status TEXT,                       -- Awaiting | Assembling | ReportPending | Done | PartiallyDone | Failed | TimedOut
  callback_sent_at TIMESTAMPTZ NULL,
  worker_id TEXT NULL,
  last_heartbeat_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ DEFAULT now(),
  updated_at TIMESTAMPTZ NULL,        -- bumped on every status transition (§5.6 Reporter, §5.7 TimeoutSweeper)
  cleaned_at TIMESTAMPTZ NULL         -- set by Reporter after terminal-job artifact deletion (§6.6); NULL = not yet cleaned
);
CREATE INDEX job_active ON job(status) WHERE status IN ('Awaiting','Assembling','ReportPending');
CREATE INDEX job_cleanup ON job(updated_at) WHERE cleaned_at IS NULL;

expected_file (
  id UUID PRIMARY KEY,
  job_id UUID FK,
  original_relative_path TEXT,
  original_format TEXT,
  applied_conversion TEXT NULL,
  expected_chunk_count INT,
  received_chunk_count INT DEFAULT 0,
  bytes_total BIGINT,
  status TEXT,                       -- Pending | Assembled | ReverseConverting | Finalized | Failed | NotSupported
  failure_reason TEXT NULL,          -- carried from manifest for files marked Failed/NotSupported by A
  worker_id TEXT NULL,
  last_heartbeat_at TIMESTAMPTZ NULL,
  UNIQUE (job_id, original_relative_path)
);

expected_chunk (
  id UUID PRIMARY KEY,
  expected_file_id UUID,
  job_id UUID,
  index INT,
  name TEXT,                         -- proxy filename; used to resolve against proxy_delivery_dir at concat time
  byte_length BIGINT,
  received_at TIMESTAMPTZ NULL,
  UNIQUE (expected_file_id, index)
);
CREATE INDEX expected_chunk_pending ON expected_chunk(expected_file_id) WHERE received_at IS NULL;

outbox (
  id BIGSERIAL PK,
  queue TEXT NOT NULL,
  payload JSONB NOT NULL,
  published_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX outbox_pending ON outbox(created_at) WHERE published_at IS NULL;

phase_config (
  phase TEXT PRIMARY KEY,
  heartbeat_interval_s INT NOT NULL DEFAULT 10,
  sweeper_threshold_s INT NOT NULL DEFAULT 30,
  max_retries INT NOT NULL DEFAULT 5
);
```

Partitioning deferred to year 2.

### 5.3 ProxyListener branches and the chunk/manifest interaction

This section is the textual companion to [`chunk-flow.drawio`](chunk-flow.drawio). ProxyListener classifies each incoming proxy event by filename pattern and executes one of seven branches, all in a single transaction. **No file copies anywhere.**

Filename patterns:
- `{job_id}.job.json` → JobHeader
- `{job_id}_{source_file_id}.file.json` → FileManifest
- `{job_id}_{source_file_id}_chunk_{index}.*` → Chunk
- `*.job.ERROR.txt` → JobHeaderError
- `*.file.ERROR.txt` → FileManifestError
- `*_chunk_*.ERROR.txt` → Error (unchanged)
- `*.UNSUPPORTED.txt` → Unsupported (unchanged)

---

**Branch ① — Chunk BEFORE file-manifest (no `expected_chunk` row for this chunk name):**

```sql
SELECT 1 FROM expected_chunk WHERE name = $chunk_name;
-- 0 rows → buffer the chunk
INSERT INTO inbox(file_path, kind='Chunk', job_id, published=FALSE)
  ON CONFLICT (file_path) DO NOTHING;
-- No outbox row. File stays at proxy delivery dir; no copy.
COMMIT; ack;
```

---

**Branch ② — JobHeader arrival:**

```sql
INSERT INTO inbox(file_path, kind='JobHeader', job_id, published=TRUE)
  ON CONFLICT (file_path) DO NOTHING;
INSERT INTO outbox(queue='job-headers.received', payload={file_path, job_id});
COMMIT; ack;
```

Assembly.Worker consuming `job-headers.received` creates the job row, then drains any buffered rows in **bounded batches** — one transaction per batch, never one giant transaction. `DRAIN_BATCH` (default 500 file-manifests per tx) is configurable.

```sql
-- (i) Create the job row in its OWN committed tx, so it is durable before any
--     drain and the drain can resume idempotently after a crash.
INSERT INTO job(id, expected_file_count, status='Awaiting', job_header_received_at=now(), ...)
  ON CONFLICT (id) DO NOTHING;
COMMIT;

-- (ii) Drain buffered FileManifest rows in batches. Each batch is its own tx, so
--      locks are held for at most one batch — not for the whole backlog.
LOOP:                                              -- DRAIN_BATCH = 500 (config)
  BEGIN;
  WITH batch AS (
    SELECT file_path, source_file_id FROM inbox
     WHERE job_id = $job_id AND kind = 'FileManifest' AND published = FALSE
     ORDER BY received_at
     LIMIT $DRAIN_BATCH
     FOR UPDATE SKIP LOCKED
  )
  UPDATE inbox SET published = TRUE
   WHERE file_path IN (SELECT file_path FROM batch)
   RETURNING source_file_id;                       -- $n = rows drained this batch
  -- For each drained file-manifest: read JSON from disk,
  --   INSERT expected_file + expected_chunk ON CONFLICT DO NOTHING (same as Branch ③).
  -- Drain buffered Chunk rows belonging ONLY to this batch's expected_files:
  UPDATE expected_chunk ec SET received_at = i.received_at
    FROM inbox i
   WHERE i.job_id = $job_id AND i.kind = 'Chunk' AND i.published = FALSE
     AND ec.expected_file_id = ANY($batch_expected_file_ids)
     AND ec.name = filename(i.file_path) AND ec.received_at IS NULL;
  UPDATE inbox SET published = TRUE
   WHERE job_id = $job_id AND kind = 'Chunk' AND published = FALSE
     AND filename(file_path) IN (
       SELECT name FROM expected_chunk WHERE expected_file_id = ANY($batch_expected_file_ids));
  -- Recompute received_chunk_count for this batch's files; flip complete files to
  --   Assembled; concat each; INSERT outbox(queue='files.assembled').
  COMMIT;
  EXIT WHEN $n < $DRAIN_BATCH;                      -- no more buffered file-manifests

-- (iii) Drain buffered FileManifestError rows in the same bounded-batch manner.
LOOP:
  BEGIN;
  WITH batch AS (
    SELECT file_path, source_file_id FROM inbox
     WHERE job_id = $job_id AND kind = 'FileManifestError' AND published = FALSE
     ORDER BY received_at LIMIT $DRAIN_BATCH FOR UPDATE SKIP LOCKED
  )
  UPDATE inbox SET published = TRUE
   WHERE file_path IN (SELECT file_path FROM batch)
   RETURNING source_file_id;                        -- $m = rows drained this batch
  -- For each: INSERT expected_file(status='Failed', expected_chunk_count=0,
  --             failure_reason='ProxyDeliveryFailure', ...) ON CONFLICT DO NOTHING;
  --           INSERT outbox(queue='files.finalized', payload={expected_file_id, job_id, status='Failed'});
  -- (job row now exists, so the expected_file.job_id FK is satisfied.)
  COMMIT;
  EXIT WHEN $m < $DRAIN_BATCH;

-- (iv) Once all buffered rows are drained, flip the job to Assembling, then ack.
UPDATE job SET status = 'Assembling' WHERE id = $job_id AND status = 'Awaiting';
COMMIT; ack;
```

> **Why batched (B9):** when the job-header is delayed, many file-manifests and chunks buffer. A single drain tx would hold locks for seconds and cascade at load. Bounding each tx to `DRAIN_BATCH` rows caps lock-hold time regardless of backlog size. **Crash-safety:** the job row is committed first (step i) and each batch commits independently; on job-header redelivery the handler re-runs — `INSERT ... ON CONFLICT DO NOTHING` is a no-op and already-drained rows are `published=TRUE` and skipped, so the loop simply resumes on the remainder. The normal path (job-header first) drains zero rows: one batch returns `$n=0 < DRAIN_BATCH`, the loop exits immediately, and the handler stays cheap.

---

**Branch ③ — FileManifest arrival (job row already exists):**

```sql
SELECT 1 FROM job WHERE id = $job_id;
-- found
INSERT INTO inbox(file_path, kind='FileManifest', job_id, source_file_id, published=TRUE)
  ON CONFLICT (file_path) DO NOTHING;
INSERT INTO outbox(queue='file-manifests.received', payload={file_path, job_id, source_file_id});
COMMIT; ack;
```

Assembly.Worker consuming `file-manifests.received` runs a single transaction:

```sql
-- (i) Read per-file manifest JSON from disk.
--   if entry.status in ('Failed','NotSupported'):
--     INSERT INTO expected_file(status, failure_reason, expected_chunk_count=0, ...) ON CONFLICT DO NOTHING
--     -- no expected_chunk inserts; emit outbox(files.finalized) directly
--   else:
INSERT INTO expected_file(status='Pending', expected_chunk_count=N, ...)
  ON CONFLICT (job_id, original_relative_path) DO NOTHING;
INSERT INTO expected_chunk(...) ON CONFLICT (expected_file_id, index) DO NOTHING;
-- for each chunk

-- (ii) INLINE DRAIN — match buffered chunks for this file.
UPDATE expected_chunk ec
   SET received_at = i.received_at
  FROM inbox i
 WHERE i.job_id = $job_id AND i.kind = 'Chunk' AND i.published = FALSE
   AND ec.expected_file_id = $expected_file_id
   AND ec.name = filename(i.file_path) AND ec.received_at IS NULL;

UPDATE inbox SET published = TRUE
 WHERE job_id = $job_id AND kind = 'Chunk' AND published = FALSE
   AND filename(file_path) IN (SELECT name FROM expected_chunk WHERE expected_file_id = $expected_file_id);

-- (iii) Recompute received_chunk_count; if complete, run concat, INSERT outbox(files.assembled).
-- (iv) Flip job status Awaiting→Assembling on first file-manifest processed.
UPDATE job SET status = 'Assembling' WHERE id = $job_id AND status = 'Awaiting';
COMMIT; ack;
```

---

**Branch ④ — FileManifest arrival (no job row yet):**

```sql
SELECT 1 FROM job WHERE id = $job_id;
-- 0 rows → buffer
INSERT INTO inbox(file_path, kind='FileManifest', job_id, source_file_id, published=FALSE)
  ON CONFLICT (file_path) DO NOTHING;
-- No outbox row. Will be drained by Branch ② job-header handler.
COMMIT; ack;
```

---

**Branch ⑤ — Chunk AFTER file-manifest (`expected_chunk` row exists):**

```sql
SELECT 1 FROM expected_chunk WHERE name = $chunk_name;
-- found
INSERT INTO inbox(file_path, kind='Chunk', job_id, published=TRUE)
  ON CONFLICT (file_path) DO NOTHING;
INSERT INTO outbox(queue='chunks.received', payload={file_path, job_id});
COMMIT; ack;
```

Assembly.Worker consuming `chunks.received`:

1. Lookup `expected_chunk` by name.
   - No row found → rare race (file-manifest committed but not yet visible to this replica). NACK to `*.retry`.
   - Terminal job (`Done`/`Failed`/`TimedOut`) → ack and drop late chunk.
   - `received_at` already set → already processed, ack and skip.
2. In a single tx:

   ```sql
   UPDATE expected_chunk
      SET received_at = now()
    WHERE id = $1 AND received_at IS NULL
    RETURNING id;
   -- only if RETURNING produced a row:
   UPDATE expected_file
      SET received_chunk_count = received_chunk_count + 1,
          status = CASE WHEN received_chunk_count + 1 = expected_chunk_count
                        THEN 'Assembled' ELSE status END
    WHERE id = $2 AND status = 'Pending'
    RETURNING status;
   -- if status flipped to 'Assembled':
   --   read all chunks for this file from proxy_delivery_dir (resolve by expected_chunk.name)
   --   concat in index order → work/{job_id}/assembled/{file_id}.bin via .tmp+rename
   --   INSERT INTO outbox(queue='files.assembled', payload={expected_file_id, job_id});
   ```

3. COMMIT, ack.

---

**Branch ⑥ — FileManifest ERROR sentinel (`{job_id}_{source_file_id}.file.ERROR.txt`):**

Mirrors the FileManifest branches ③/④: publish immediately only if the job row exists; otherwise buffer and let the job-header handler drain it. This prevents the `expected_file.job_id` FK violation that occurs when an error sentinel is delivered before its job-header (independent proxy deliveries are not ordered — see §5 reliability note).

**Branch ⑥a — error sentinel, job row already exists:**

```sql
SELECT 1 FROM job WHERE id = $job_id;
-- found
INSERT INTO inbox(file_path, kind='FileManifestError', job_id, source_file_id, published=TRUE)
  ON CONFLICT (file_path) DO NOTHING;
INSERT INTO outbox(queue='file-manifest-errors.received',
                   payload={job_id, source_file_id, failure_reason='ProxyDeliveryFailure'});
COMMIT; ack;
```

**Branch ⑥b — error sentinel, no job row yet (buffer):**

```sql
SELECT 1 FROM job WHERE id = $job_id;
-- 0 rows → buffer
INSERT INTO inbox(file_path, kind='FileManifestError', job_id, source_file_id, published=FALSE)
  ON CONFLICT (file_path) DO NOTHING;
-- No outbox row. Will be drained by Branch ② job-header handler.
COMMIT; ack;
```

Assembly.Worker consuming `file-manifest-errors.received` (published by Branch ⑥a, or by the Branch ② drain):

```sql
INSERT INTO expected_file(status='Failed', expected_chunk_count=0,
                          failure_reason='ProxyDeliveryFailure', ...)
  ON CONFLICT DO NOTHING;
INSERT INTO outbox(queue='files.finalized', payload={expected_file_id, job_id, status='Failed'});
COMMIT; ack;
```

Reporter handles `files.finalized` normally — increments `finalized_file_count`, checks completion.

---

**Branch ⑦ — JobHeader ERROR sentinel (`{job_id}.job.ERROR.txt`):**

```sql
INSERT INTO inbox(file_path, kind='JobHeaderError', job_id, published=TRUE)
  ON CONFLICT (file_path) DO NOTHING;
-- Audit only. Cannot recover without job metadata.
-- No job row exists, so the TimeoutSweeper never sees this job.
-- This row (and any buffered FileManifest/Chunk/FileManifestError rows for the
-- same job) are reaped by the age-based inbox-cleanup cron (§6.6).
COMMIT; ack;
```

No outbox row. Recovery is not possible without job metadata. Because no `job` row is ever created, the TimeoutSweeper (which scans `job` rows only) cannot touch these orphaned `inbox` rows — they are cleaned solely by the age-based inbox-cleanup cron in §6.6.

---

**Concat idempotency:** the output path is deterministic (`work/{job_id}/assembled/{file_id}.bin`); `.tmp + rename` makes partial files invisible. A redelivery + winning CAS at a different worker would recompute identical bytes from the same chunk inputs and rename them into place — the final state is identical.

**Reliability note:** per-file manifests mean N+1 proxy deliveries per job instead of 1. Each delivery is an independent failure point. The proxy drops ERROR sentinels on delivery failure (handled by Branches ⑥/⑦). If a file-manifest is silently lost without an ERROR sentinel, the TimeoutSweeper's 30-minute SLA covers it — the same risk level as the old single-manifest loss.

### 5.4 Idempotency summary

- `inbox.file_path` PK dedups proxy redeliveries.
- `inbox.published` flag is the buffer / drained marker.
- `expected_chunk.received_at` CAS gate prevents double counter increment.
- `expected_file.status='Pending'` WHERE-clause: only one worker flips to `Assembled` and publishes.
- Manifest upsert WHERE-clause refuses to resurrect terminal jobs (`WHERE job.status='Awaiting'`).
- Concat output path deterministic; `.tmp + rename` ensures no partial files.
- All next-step publishes go through `outbox` + OutboxRelay (publisher-confirmed).

### 5.5 Reverse conversion & finalization

ReverseConverter consumes `files.assembled`:
- CAS `expected_file.status Assembled→ReverseConverting`.
- Reads `work/{job_id}/assembled/{file_id}.bin`.
- Applies reverse `IFileConverter` per `applied_conversion` (or pass-through).
- Writes result to `target_path/<original_relative_path>` via `.tmp + rename`.
- CAS `expected_file.status ReverseConverting→Finalized`.
- Inserts `outbox(queue='files.finalized', payload)`.

ReverseConverter does NOT touch the proxy delivery dir or per-chunk bytes — concat is Assembly's job.

### 5.6 Reporting & callback

Reporter consumes `files.finalized`, `chunks.error`, `chunks.unsupported`. For each terminal flip, in one transaction:

```sql
UPDATE expected_file
   SET status = $new_terminal_status   -- 'Finalized' | 'Failed' | 'NotSupported'
 WHERE id = $expected_file_id
   AND status NOT IN ('Finalized','Failed','NotSupported')
 RETURNING id;

UPDATE job
   SET finalized_file_count = finalized_file_count + 1,
       status = CASE WHEN finalized_file_count + 1 = expected_file_count
                      THEN 'ReportPending' ELSE status END,
       updated_at = now()
 WHERE id = $job_id
 RETURNING finalized_file_count, expected_file_count, status;
```

When `status='ReportPending'`:
1. Build `StatusCallbackPayload`. The callback `JobStatus` (frozen external contract) and the internal `job.status` it maps to:
   | File outcomes | Callback `JobStatus` | Internal `job.status` |
   |---------------|----------------------|-----------------------|
   | All `Finalized` | `Completed` | `Done` |
   | Some `Failed`/`NotSupported` (≥1 finalized) | `CompletedPartially` | `PartiallyDone` |
   | All `Failed`/`NotSupported` | `Failed` | `Failed` |
   | Manifest never arrived | `Timeout` (set by `TimeoutSweeper`, §5.7) | `TimedOut` |
2. Write `{job_id}_report.csv` (header `status,dir_path`, values `COMPLETED|FAILED|NOT_SUPPORTED`) via `.tmp + rename`.
3. Dispatch via `IAnswerDispatcher`.
4. Final CAS — set the internal terminal status mapped from the computed `JobStatus` above (`$terminal_status` ∈ `Done | PartiallyDone | Failed`):

   ```sql
   UPDATE job
      SET status=$terminal_status, callback_sent_at=now()
    WHERE id=$1 AND status='ReportPending' AND callback_sent_at IS NULL
    RETURNING 1;
   ```

`ReportPendingSweeper` retries steps 1–4 for stuck `ReportPending` rows older than 5 min.

> **Callback delivery is at-least-once, NOT exactly-once.** Step 3 (dispatch) is an external side effect that cannot be made atomic with step 4 (the DB commit recording `callback_sent_at`). If the worker crashes between 3 and 4, `callback_sent_at` is still NULL, so `ReportPendingSweeper` re-runs 1–4 and **re-dispatches the callback**. The `callback_sent_at IS NULL` guard makes the *status transition* exactly-once, but not the external delivery. **The consuming system must be idempotent** — ignore a repeated `StatusCallbackPayload` for a `jobId`/`externalId` it has already processed (see QUESTIONS §3). The CSV write (step 2) is naturally idempotent via `.tmp + rename`.

### 5.7 Timeout handling

`TimeoutSweeper` (cron in Reporter, sweeper-enabled replica) runs every 60s:

```sql
WITH t AS (
  SELECT id FROM job
   WHERE status IN ('Awaiting','Assembling')
     AND now() - coalesce(job_header_received_at, created_at) > interval '30 minutes'
   LIMIT 10 FOR UPDATE SKIP LOCKED
)
UPDATE job SET status='ReportPending', updated_at=now()
  WHERE id IN (SELECT id FROM t) AND status IN ('Awaiting','Assembling')
  RETURNING id;
```

For each timed-out job: flip non-terminal `expected_file` rows to `Failed`, build payload with `JobStatus='Timeout'`, write CSV + dispatch callback, final CAS to `TimedOut`.

**SLA = 30 minutes** from `coalesce(job_header_received_at, created_at)`. Configurable per deployment.

**Terminal short-circuit:** every B-worker checks `job.status` before doing work. Terminal → ack and exit. Prevents late chunks from producing files in `target_path` after a failure callback has gone out.

### 5.8 Retry policy (NetworkB) — aligned with `worker-exception-handling.drawio`

Same shape as NetworkA §3.5:

| Class | Example exception types | Action |
|-------|--------------------------|--------|
| **Transient** | `SqlException`, `BrokerUnreachableException`, `IOException`, `TimeoutException`, `OperationCanceledException` (non-shutdown) | `nack(requeue=false)` → `*.retry` queue (1-min TTL) → redelivery up to `max_retries=5` |
| **Permanent** | `UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException` | `UPDATE expected_file SET status='Failed', failure_reason=<message>` directly → `nack(requeue=false)` → `*.dead`. Job continues for other files. |
| **Unknown** | Anything else | Transient by default. |

- After `max_retries=5` → DLQ consumer flips `expected_file.status='Failed'`.
- **Job marked `Failed` ONLY by:** Reporter (all `expected_file` Failed), TimeoutSweeper (SLA exceeded → `TimedOut`), or Reporter resolving `JobStatus='Timeout'`. Workers NEVER mark `job.status='Failed'` directly.

See [`worker-exception-handling.drawio`](worker-exception-handling.drawio) for the decision tree.

### 5.9 ProxyListener stability check

Two-stat synchronous: `stat` → sleep 200ms → `stat`, compare size + mtime.

---

## 6. Cross-cutting Concerns

### 6.1 Configuration

- Each network has a `config` table for proxy rules, converter mappings, retry counts, timeout durations, outbox paths.
- Workers snapshot the rule active at job-creation time into the `job` row.
- Heartbeat / sweeper threshold / max_retries per phase live in `phase_config` (defaults: 10s / 30s / 5).

### 6.2 Idempotency keys

- Primary: **per-row status CAS** with `worker_id` + `last_heartbeat_at` on every state flip.
- Secondary: UNIQUE constraints on natural business keys.
- Job-level: `IngestionRequestPayload.ExternalId` is unique-indexed in Postgres-A `job`.
- Outbox: `outbox.id` is the publish identity; relays are at-least-once (consumers remain idempotent via CAS).

### 6.3 Observability

- **Structured logs (Serilog)** — `job_id`, `service`, `step`, `worker_id`, `message_id`.
- **Metrics (Prometheus)** — RED per worker step; queue depths; `outbox` backlog (pending rows + max age) per network; sweeper rescue counts; Postgres pool / autovacuum / replication lag.
- **Tracing (OpenTelemetry)** — `job_id` as baggage.
- **DLQ alerts** — any `*.dead` message > 5 min pages.
- **Outbox alerts** — outbox backlog > N for > N min pages.
- **Heartbeat-staleness alerts** — sweeper rescue rate above baseline indicates churning workers.

### 6.4 Scaling for 1M jobs/month

- **Workers** scale horizontally per phase.
- **OutboxRelay** — 2–3 replicas per network competing via `FOR UPDATE SKIP LOCKED`.
- **RabbitMQ** — quorum queues, single broker per network. Topology as `definitions.json`; tunables as policies.
- **Postgres** — no partitioning day-1. Year-2: monthly partitioning on `chunk`, `expected_chunk`, `outbox`. PgBouncer in transaction-pooling mode.
- **Sweepers** — single sweeper-enabled replica per service (`SWEEPER_ENABLED=true`). Per-row CAS gates make duplicate sweepers safe.

### 6.5 Storage tier choice

| Volume | Default | Why |
|--------|---------|-----|
| **A `work/{job_id}/`** | Local PV (RWX or RWO) | Throughput modest at 4 jobs/sec. |
| **A outbox dirs (data + manifest)** | Local filesystem | Proxy contract — filesystem-watched. |
| **B proxy delivery dir** | Local PV (RWX shared by ProxyListener + Assembly) | Chunks live here for the full job lifetime. Assembly reads in-place during concat. Owned and cleaned up by NetworkB. |
| **B `work/{job_id}/assembled/`** | Local PV | Assembled file output (concat target). |
| **B target_path** | Filesystem | Client interface. |

`IStorage` abstracts binding (`Local` vs `S3`).

### 6.6 Retention & cleanup

- **A:** when proxy completes pickup, Split.Worker's cleanup cron deletes chunk files and `work/{job_id}/`. Backup cron sweeps outbox dirs older than `proxy_max_transit + safety_margin`.
- **B (terminal job):** Reporter (on `job.status IN ('Done','Failed','TimedOut','PartiallyDone')`) deletes:
  1. `work/{job_id}/assembled/` (assembled intermediates)
  2. All chunk files in the proxy delivery dir referenced by `expected_chunk.name` for this job (chunks are not needed once the job is terminal)
  3. Job-header file and all per-file manifest files in the proxy delivery dir for this job
  After deletes succeed, sets `job.cleaned_at=now()`.
- Backup cron: `cleaned_at IS NULL AND status IN ('Done','Failed','TimedOut','PartiallyDone') AND updated_at < now() - 1d`.
- **Postgres:** cleanup cron DELETEs terminal-job rows older than retention. Year-2: monthly partitioning + `DROP PARTITION`.
- **Outbox rows:** deleted N days after `published_at` IS NOT NULL.
- **`inbox` rows (Network B):** a single cleanup cron deletes by age regardless of `kind` or `published`:
  ```sql
  DELETE FROM inbox WHERE received_at < now() - interval 'N days';   -- N = inbox_retention_days, default 7
  ```
  This covers both (a) **terminal rows** (`published=TRUE`) of completed jobs, and (b) **orphaned rows** that never get a `job` row — `JobHeaderError` audit rows plus any `FileManifest`/`Chunk`/`FileManifestError` rows that buffered (`published=FALSE`) for a job whose header permanently failed. `N days` must exceed the job SLA (30 min) by a wide margin so a job that is still going to materialize always has, before its rows are reaped. The TimeoutSweeper (§5.7) does **not** clean these — it only scans rows with an existing `job` row; orphaned `inbox` rows have none, which is exactly why this dedicated cron exists.

### 6.7 Schema migration

- OpenShift Job (Helm pre-upgrade hook) per network owns migrations.
- Online migrations only: `ADD COLUMN NULL` → backfill job → `SET NOT NULL` in later release.

### 6.8 Secrets

- OpenShift Secrets via Sealed Secrets / external-secrets-operator.

### 6.9 Crash resilience checklist

- [x] Every worker step is idempotent via per-row status CAS.
- [x] Heartbeat refreshes `last_heartbeat_at` (10s) while work is in flight.
- [x] Phase sweeper resets stale rows whose heartbeat lapsed beyond 30s.
- [x] Next-step publishes routed through transactional outbox → OutboxRelay (~500ms).
- [x] Assembly concat happens before COMMIT and before outbox row insert → no publish-vs-file timing race.
- [x] On-disk artifacts have deterministic names → safe rewrites with identical bytes.
- [x] Counters incremented inside the same tx as the child CAS; only the CAS winner increments.
- [x] All side-effects gated by terminal-status short-circuit.
- [x] ProxyListener inbox PK on `file_path` dedups proxy redeliveries; `published` flag is the buffer / drain marker.
- [x] File-manifest handler does inline-drain of its own buffered chunks in one tx; the job-header handler drains buffered rows in **bounded batches** (`DRAIN_BATCH`=500/tx, §5.3) so a delayed-header backlog never holds locks for one giant tx. Job row committed first; each batch commits independently → drain resumes idempotently on redelivery.
- [x] Per-file mini-manifest written BEFORE committing terminal DB status → heartbeat sweeper recovery covers crash between file-write and commit.
- [x] `manifest_written` flag on `source_file` prevents double-counting across retries; sweeper never resets it.
- [x] DLQ consumer writes mini-manifest (if `manifest_written=FALSE`) before flipping status; same write-before-commit sequencing.
- [x] Per-file mini-manifest carries per-file failure status so NB never waits for chunks that will never arrive.

---

## 7. What's removed vs. current

| Removed | Why |
|---------|-----|
| Temporal (workflows, signals, activities) | Replaced by Rabbit + Postgres + idempotent workers. |
| SHA256 compute & verify | Per user instruction; deterministic chunk names + UNIQUE constraints provide placement integrity. |
| `.tmp` + rename on proxy outbox | Per user instruction; staging-subdir + mv to final filename. |
| Global "wait for all chunks of a package" | Per user instruction; per-file batch finalization. |
| Repack of nested archives on NetworkB | Per user instruction; assembled files placed flat under `target_path`. |
| `processed_message`, `counter_event`, `file_finalized_dedup`, `split_completed` | Replaced by per-row status CAS. |
| Advisory locks, consistent-hash exchange, leader-elected sweepers, day-1 partitioning, session-pool PgBouncer route, path-sharded work dirs, async two-stat stability check | Deferred. |
| NACK+requeue for chunks-before-manifest | Replaced by inbox buffering (`published=FALSE`) + manifest-time inline drain. |
| Per-chunk holding files in NetworkB work dir | Replaced by reading chunks in-place from the proxy delivery dir during concat. |
| Single end-of-job manifest | Replaced by job header (`{job_id}.job.json` written by Prepare) + per-file mini-manifests (`{job_id}_{source_file_id}.file.json` written by Split). NB starts assembling each file as soon as its mini-manifest and chunks arrive. |
| `ManifestPending` / `ManifestWritten` job states | Replaced by `files_manifest_written_count` counter + `AllManifestsWritten`. No single "last writer" coordination; each Split.Worker handles its own file independently. |
| ManifestSweeper | Eliminated. Per-file write-before-commit sequencing + heartbeat sweeper recovery covers all crash scenarios without a dedicated sweeper. |

---

## 8. What's preserved

- `IngestionRequestPayload`
- `StatusCallbackPayload` (verbatim shape)
- Final CSV format: header `status,dir_path`, values `COMPLETED|FAILED|NOT_SUPPORTED`
- Proxy Rabbit message: `{ filePath }`
- Splitters / Converters / Assemblers / Reverse-Converters business logic
- Dual outbox directories (data + manifest) on A → proxy
- Configuration-driven processing rules

---

## 9. Repo layout (proposed)

```
src/
  NetworkA/
    Dintinct.A.Ingestion.Api/
    Dintinct.A.Ingestion.FolderWatcher/
    Dintinct.A.Ingestion.RabbitBridge/
    Dintinct.A.Ingestion.RequestFileWatcher/
    Dintinct.A.Prepare.Worker/
    Dintinct.A.Convert.Worker/
    Dintinct.A.Split.Worker/                  # writes per-file mini-manifests to proxy outbox; Prepare writes job header
    Dintinct.A.OutboxRelay/                   # relays outbox rows → RabbitMQ
    Dintinct.A.DlqConsumer/                   # binds *.dead; write-before-commit mini-manifest + source_file Failed
    Dintinct.A.Domain/
    Dintinct.A.FileProcessing/
    Dintinct.A.Infrastructure/
  NetworkB/
    Dintinct.B.ProxyListener/
    Dintinct.B.Assembly.Worker/
    Dintinct.B.ReverseConverter.Worker/
    Dintinct.B.Reporter.Worker/
    Dintinct.B.OutboxRelay/
    Dintinct.B.DlqConsumer/                   # binds *.dead; expected_file Failed (no manifest write on B)
    Dintinct.B.Domain/
    Dintinct.B.FileAssembly/
    Dintinct.B.Infrastructure/
  Shared/
    Dintinct.Shared.Contracts/
    Dintinct.Shared.Messaging/
    Dintinct.Shared.Storage/
    Dintinct.Shared.Heartbeat/                # heartbeat loop helper used by every worker
    Dintinct.Shared.Outbox/                   # outbox writer + relay base
deploy/
  openshift/
    network-a/
    network-b/
    proxy/
db/
  network-a/migrations/
  network-b/migrations/
rabbit/
  network-a/definitions.json
  network-b/definitions.json
docs/
  ARCHITECTURE.md
  MICROSERVICES.md
  SUMMARY.md
  QUESTIONS-TO-CLIENT.md
  architecture-v3.drawio
  cas-heartbeat-recovery.drawio
  chunk-flow.drawio
  worker-exception-handling.drawio
```

---

## 10. Open Decisions

All in-document clarifications resolved. Remaining client-facing items in [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md):

1. Proxy-outbox safe-write strategy (staging-subdir + mv vs. proxy debounce).
2. Average files-per-job (drives year-2 partitioning + ingestion-split trade-off).
3. Storage retention SLA per volume.
4. ~~Converter cardinality~~ — ✅ answered **1→1** (tables collapsed; see §3.2/§3.4).
5. Confirm 4-channel ingestion split.
6. Local-PV vs. S3 storage default.

Resolved in this revision (recorded for traceability):

| # | Item | Resolution |
|---|------|------------|
| 1 | Outbox scope | Both A and B. OutboxRelay per network. |
| 2 | Heartbeat intervals | 10s all phases. Sweeper threshold 30s. Configurable via `phase_config`. |
| 3 | `max_retries` default | 5. |
| 4 | A-side Failed analog | `source_file.status='Failed'`. Per-file mini-manifest carries failure status so NB creates `expected_file` terminal directly. |
| 5 | Transient/permanent | Exception-type whitelist (§3.5, §5.8). Unknown → transient. |
| 6 | Buffered chunk byte location | Read in-place from proxy delivery dir; no copy. RWX PV shared by ProxyListener + Assembly. |
| 7 | Concat owner | Assembly, inside the winning-CAS handler, before COMMIT (file IO sequenced before outbox insert). |
| 8 | Timeout SLA | 30 minutes from `coalesce(job_header_received_at, created_at)`. |
| 9 | End-of-job manifest | Replaced by job header (Prepare) + per-file mini-manifests (Split). NB can start assembling each file immediately on mini-manifest arrival; no global wait. |
| 10 | Manifest write crash safety | Write-before-commit sequencing: mini-manifest written to disk BEFORE committing terminal DB status. Heartbeat sweeper reset → retry → re-write (idempotent) → commit. `manifest_written` flag gates counter increment (sweeper never resets it). |
| 11 | NB job row creation trigger | Job header arrival (via Proxy) — `job_header_received_at` replaces `manifest_received_at`. `expected_file_count` set from job header's `totalSourceFiles`. |
| 12 | ProxyListener branch count | 7 branches (was 3): chunk-before-file-manifest, job-header, file-manifest (job exists), file-manifest (no job), chunk-after-file-manifest, file-manifest ERROR sentinel, job-header ERROR sentinel. |
