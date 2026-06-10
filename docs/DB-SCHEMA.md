# Database Schema

Both networks use identical structural patterns (CAS + heartbeat + outbox) on different domain entities.

- `worker_id` + `last_heartbeat_at` — on every table where a worker claims a row for long-running work; sweeper resets stale rows via `WHERE last_heartbeat_at < now() - threshold`.
- `outbox` — transactional outbox in same DB; relay publishes to Rabbit after COMMIT, covers post-COMMIT crash. `id BIGSERIAL` so relay FIFO via `ORDER BY id`.
- `config` — key/value table for proxy rules, converter mappings, timeout durations, outbox paths.
- `phase_config` — dedicated table for per-phase tuning (heartbeat, sweeper threshold, max_retries). Defaults: 10s / 30s / 5.
- `failure_reason TEXT NULL` on `source_file` (A) and `expected_file` (B) — carried into manifest (A→B) or callback (B) when a row goes terminal `Failed`/`NotSupported`.

---

## Network A — Postgres-A

### `config`

```sql
config (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
```

Sample keys: `outbox.data_path`, `outbox.manifest_path`, `proxy_rule.<format>`, `converter.<format>`, `timeout_sla_minutes`.

Per-phase tuning lives in `phase_config` below — not in this table.

---

### `phase_config`

```sql
phase_config (
  phase                TEXT PRIMARY KEY,    -- 'Prepare' | 'Convert' | 'Split' | 'ProxyListener' | 'Assembly' | 'ReverseConvert' | 'Report'
  heartbeat_interval_s INT NOT NULL DEFAULT 10,
  sweeper_threshold_s  INT NOT NULL DEFAULT 30,    -- 3 × heartbeat_interval
  max_retries          INT NOT NULL DEFAULT 5
);
```

Same table on both networks; rows differ per phase.

---

### `job`

```sql
job (
  id                    UUID PRIMARY KEY,
  external_id           TEXT UNIQUE,
  request_payload       JSONB,              -- verbatim IngestionRequestPayload, for audit/replay
  source_path           TEXT,
  target_path           TEXT,
  target_network        TEXT,
  calling_system_id     TEXT,
  calling_system_name   TEXT,
  answer_type           TEXT,
  answer_location       TEXT,
  package_type          TEXT,               -- File | Folder | Archive
  original_package_name TEXT,
  total_source_files    INT,                -- set after Prepare
  files_manifest_written_count INT NOT NULL DEFAULT 0,   -- atomic counter; the sole AllManifestsWritten gate
  status                TEXT,              -- Created | Preparing | Prepared | Splitting | AllManifestsWritten | Failed
  worker_id             TEXT NULL,
  last_heartbeat_at     TIMESTAMPTZ NULL,
  created_at            TIMESTAMPTZ,
  updated_at            TIMESTAMPTZ
);
CREATE INDEX job_status_active ON job(status) WHERE status NOT IN ('AllManifestsWritten','Failed');
CREATE INDEX job_stale_hb ON job(last_heartbeat_at) WHERE last_heartbeat_at IS NOT NULL;
```

---

### `source_file`

```sql
source_file (
  id                     UUID PRIMARY KEY,
  job_id                 UUID REFERENCES job(id),
  original_relative_path TEXT,
  original_format        TEXT,
  applied_conversion     TEXT NULL,
  converted_relative_path TEXT NULL,       -- 1→1 converter output (Q11); = original_relative_path for pass-through
  status                 TEXT,             -- Pending | Converting | Converted | Splitting | Split | Failed | NotSupported
  failure_reason         TEXT NULL,        -- populated on terminal Failed/NotSupported; carried into manifest
  manifest_written       BOOLEAN NOT NULL DEFAULT FALSE,   -- set when mini-manifest written to proxy outbox; sweeper does NOT reset this column
  worker_id              TEXT NULL,
  last_heartbeat_at      TIMESTAMPTZ NULL,
  UNIQUE (job_id, original_relative_path)
);
CREATE INDEX source_file_stale_hb ON source_file(last_heartbeat_at) WHERE last_heartbeat_at IS NOT NULL;

-- Note: heartbeat sweeper resets status/worker_id/last_heartbeat_at but NOT manifest_written (permanent once written to proxy outbox).
```

---

### ~~`converted_file`~~ — removed

The converter is **1→1** (Q11), so conversion output folds into `source_file` (`applied_conversion` + `converted_relative_path`). Convert and Split both CAS on `source_file` — no separate table, one fewer join and CAS hop.

---

### `chunk`

```sql
chunk (
  id                UUID PRIMARY KEY,
  job_id            UUID,
  source_file_id    UUID,
  index             INT,
  name              TEXT,
  byte_length       BIGINT,
  written_at        TIMESTAMPTZ,
  UNIQUE (job_id, source_file_id, index)
);
CREATE INDEX chunk_job ON chunk(job_id);
```

> UNIQUE `(job_id, source_file_id, index)` is collision-free because the 1→1 converter yields exactly one converted file per source file (Q11) — `index` never repeats for a `source_file_id`. Resolves BLOCKERS N3.

> Terminal on insert — no worker ownership, no heartbeat columns needed.

---

### `nested_archive`

```sql
nested_archive (
  id                    UUID PRIMARY KEY,
  job_id                UUID REFERENCES job(id),
  archive_relative_path TEXT,
  parent_archive_id     UUID NULL REFERENCES nested_archive(id)
);
```

---

### `outbox`

```sql
outbox (
  id           BIGSERIAL PRIMARY KEY,   -- FIFO ordering for relay
  queue        TEXT NOT NULL,
  payload      JSONB NOT NULL,
  published_at TIMESTAMPTZ NULL,        -- NULL = pending; set after publisher-confirm
  created_at   TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX outbox_pending ON outbox(id) WHERE published_at IS NULL;
```

Relay query (every ~500ms):
```sql
SELECT id, queue, payload
  FROM outbox
 WHERE published_at IS NULL
 ORDER BY id
   FOR UPDATE SKIP LOCKED
 LIMIT 100;
```

---

## Network B — Postgres-B

### `config` / `phase_config`

Same structure as Network A `config` and `phase_config`.

---

### `job`

```sql
job (
  id                    UUID PRIMARY KEY,   -- original job_id from manifest
  source_path           TEXT,
  target_path           TEXT,
  target_network        TEXT,
  calling_system_id     TEXT,
  calling_system_name   TEXT,
  external_id           TEXT,
  answer_type           TEXT,
  answer_location       TEXT,
  package_type          TEXT,
  original_package_name TEXT,
  job_header_received_at TIMESTAMPTZ NULL,
  expected_file_count   INT NULL,
  finalized_file_count  INT DEFAULT 0,
  status                TEXT,              -- Awaiting | Assembling | ReportPending | Done | PartiallyDone | Failed | TimedOut
  worker_id             TEXT NULL,
  last_heartbeat_at     TIMESTAMPTZ NULL,
  callback_sent_at      TIMESTAMPTZ NULL,
  created_at            TIMESTAMPTZ DEFAULT now(),
  updated_at            TIMESTAMPTZ NULL,   -- bumped on every status transition (Reporter §5.6, TimeoutSweeper §5.7)
  cleaned_at            TIMESTAMPTZ NULL    -- set by Reporter after terminal-job artifact deletion (§6.6); NULL = not yet cleaned
);
CREATE INDEX job_active ON job(status) WHERE status IN ('Awaiting','Assembling','ReportPending');
CREATE INDEX job_cleanup ON job(updated_at) WHERE cleaned_at IS NULL;
CREATE INDEX job_stale_hb ON job(last_heartbeat_at) WHERE last_heartbeat_at IS NOT NULL;
```

---

### `inbox`

```sql
inbox (
  file_path   TEXT PRIMARY KEY,           -- absolute path in proxy delivery dir; unique; PK dedups proxy redeliveries.
                                           -- Chunks are NEVER copied — Assembly reads bytes in-place from this path during concat.
  received_at TIMESTAMPTZ DEFAULT now(),
  kind        TEXT,                       -- Chunk | JobHeader | FileManifest | Error | Unsupported | JobHeaderError | FileManifestError
  job_id      UUID NULL,                  -- ALWAYS parsed from the filename's leading {job_id} component (chunks, headers,
                                           -- manifests, sentinels all carry it). NULL only if the filename is unparseable.
                                           -- Must be set on buffered chunks too — the job-header drain matches WHERE job_id=$.
  source_file_id UUID NULL,               -- set for FileManifest / FileManifestError rows; NULL for JobHeader / Chunk
  published   BOOLEAN DEFAULT FALSE       -- TRUE when outbox row inserted (same tx) OR inline-drain consumed this row;
                                           -- FALSE = chunk buffered (no manifest yet)
);
CREATE INDEX inbox_unpublished_by_job ON inbox(job_id) WHERE published = FALSE;
CREATE INDEX inbox_buffered_chunks ON inbox(job_id) WHERE kind = 'Chunk' AND published = FALSE;
```

---

### `expected_file`

```sql
expected_file (
  id                     UUID PRIMARY KEY,
  job_id                 UUID REFERENCES job(id),
  original_relative_path TEXT,
  original_format        TEXT,
  applied_conversion     TEXT NULL,
  expected_chunk_count   INT,             -- 0 for files marked Failed/NotSupported at manifest time
  received_chunk_count   INT DEFAULT 0,
  bytes_total            BIGINT,
  status                 TEXT,            -- Pending | Assembled | ReverseConverting | Finalized | Failed | NotSupported
  failure_reason         TEXT NULL,       -- carried from manifest for files marked Failed/NotSupported by A
  worker_id              TEXT NULL,
  last_heartbeat_at      TIMESTAMPTZ NULL,
  UNIQUE (job_id, original_relative_path)
);
CREATE INDEX expected_file_stale_hb ON expected_file(last_heartbeat_at) WHERE last_heartbeat_at IS NOT NULL;
```

---

### `expected_chunk`

```sql
expected_chunk (
  id               UUID PRIMARY KEY,
  expected_file_id UUID,
  job_id           UUID,
  index            INT,
  name             TEXT,                  -- proxy filename; resolved against proxy delivery dir at concat time
  byte_length      BIGINT,
  received_at      TIMESTAMPTZ NULL,      -- set when the chunk file is durable in the proxy delivery dir (post-stability-check)
  UNIQUE (expected_file_id, index)
);
CREATE INDEX expected_chunk_pending ON expected_chunk(expected_file_id) WHERE received_at IS NULL;
```

> Chunk processing is atomic (single CAS + counter increment + concat-if-last). Chunk bytes are NEVER copied — Assembly reads them in-place from `proxy_delivery_dir/<name>` during concat. No long-running worker ownership on this row — no heartbeat columns.

---

### `outbox`

Same structure as Network A `outbox`.

---

## Pattern summary

| Pattern | Tables |
|---------|--------|
| CAS + heartbeat (`worker_id`, `last_heartbeat_at`, `*_stale_hb` index) | A: `job`, `source_file` · B: `job`, `expected_file` |
| Transactional outbox (`BIGSERIAL` id for FIFO) | `outbox` — both networks |
| Per-phase tuning | `phase_config` — both networks (defaults 10s / 30s / 5) |
| Counter increment (CAS-gated, same tx) | A: `job.files_manifest_written_count` · B: `job.finalized_file_count`, `expected_file.received_chunk_count` |
| UNIQUE + `ON CONFLICT DO NOTHING` | `source_file`, `chunk`, `expected_file`, `expected_chunk` |
| PK dedup | `inbox.file_path` (proxy redeliveries) |
| Chunk-before-manifest buffer | `inbox.published=FALSE` — chunk before file-manifest, file-manifest before job header, 2-level buffer, drained inline. 3-level drain on job header handler (risk accepted). |
| Failure propagation A→B | via per-file mini-manifest (`{job_id}_{source_file_id}.file.json`): `source_file.failure_reason` → mini-manifest `status`/`failureReason` → `expected_file.failure_reason` |
| Chunks read in-place | `expected_chunk.name` resolves against proxy delivery dir; no work-dir copy |

## Resolved decisions

- **No `processed_message` table.** `inbox.file_path PRIMARY KEY` provides all dedup needed. `uuid_v5(filePath)` was only relevant if `inbox` were partitioned (cross-partition PK dedup gap). Partitioning deferred to year 2; until then the PK alone is sufficient.
- **No InboxSweeper.** Transactional `outbox` table + relay covers post-COMMIT crash recovery. Chunk-before-manifest case handled by `inbox.published=FALSE` drained inline on manifest arrival (see Assembly.Worker manifest path).
- **`outbox.id BIGSERIAL`** — relay reads with `ORDER BY id` for FIFO. UUID id would break ordering.
- **`phase_config` dedicated table** (not key/value rows in `config`) — typed columns, easier `JOIN` from sweepers / metrics.
- **No `expected_chunk` heartbeat columns** — chunk handling is single-tx atomic (CAS + counter + maybe concat). Long-running ownership lives on `expected_file` instead.
- **Heartbeat indexes** (`*_stale_hb` partial index on `last_heartbeat_at WHERE last_heartbeat_at IS NOT NULL`) — supports sweeper scans without bloating during normal idle.
- **`failure_reason TEXT NULL`** on `source_file` + `expected_file` — drives the manifest-carries-failure flow (A→B) and the `StatusCallbackPayload` (B→client).
- **Chunks stay in proxy delivery dir for the full job lifetime.** Cleanup is Reporter's responsibility on terminal `job.status`.
- **No ManifestSweeper.** Per-file mini-manifests eliminate single "last writer" coordination. `manifest_written` flag on `source_file` gates idempotent counter increment (`files_manifest_written_count`).
- **`manifest_written` not reset by sweeper** — permanent by design once the file is written to proxy outbox.
