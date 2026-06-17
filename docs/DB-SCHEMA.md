# Database Schema

Target: **PostgreSQL 16** ([docs](https://www.postgresql.org/docs/16/index.html)), one cluster per network, fronted by PgBouncer (transaction pooling). Nothing in this schema is version-gated; all of `FOR UPDATE SKIP LOCKED`, `BIGSERIAL`, JSONB, and partial indexes are long-standing features.

Both networks use identical structural patterns (done-state CAS + outbox) on different domain entities.

- **Done-state CAS** — every step ends with a single `UPDATE ... SET status='Done' WHERE id=$1 AND status='Prev'`. No intermediate `-ing` states, no `worker_id`/`last_heartbeat_at` columns, no per-phase sweeper. Liveness/recovery is RabbitMQ redelivery + `consumer_timeout` (see [ARCHITECTURE.md §3.3](ARCHITECTURE.md)). The CAS is the exactly-once gate for both the status flip and any counter increment in the same tx.
- `outbox` — transactional outbox in same DB; relay publishes to Rabbit after COMMIT, covers post-COMMIT crash. `id BIGSERIAL` so relay FIFO via `ORDER BY id`.
- `config` — key/value table for genuinely **global** (non-per-caller) knobs: outbox poll interval, `DRAIN_BATCH`, retention days.
- `calling_system_config` / `conversion_rule` — **per-`callingSystemId` configuration with `default` fallback** (ARCHITECTURE.md §6.1). `conversion_rule` (A only) holds the per-`(callingSystemId, source_format)` processing rule (forward + reverse conversion, split file-size limit); `calling_system_config` (both networks) holds per-caller folder locations + SLA/timeout. SLA is independent per network (no A→B carry); the reverse-conversion instruction rides A→B in the mini-manifest.
- `phase_config` — per-phase `max_retries` (default 5). No heartbeat/sweeper columns; reserved as the home for any future per-phase escape-hatch heartbeat (ARCHITECTURE.md §3.3).
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

Sample keys (global only): `outbox.poll_interval_ms`, `drain_batch`, `inbox_retention_days`. Per-caller folders/conversion/SLA moved to `calling_system_config` + `conversion_rule` (below); per-phase tuning lives in `phase_config` — neither belongs in this table.

---

### `calling_system_config`

Per-`callingSystemId` folders + SLA, with a `default` fallback row. Same table name on both networks; A and B carry different columns (folders + A-side SLA on A; SLA + report-pending threshold on B). See ARCHITECTURE.md §6.1.

```sql
calling_system_config (
  calling_system_id    TEXT PRIMARY KEY,   -- literal 'default' row is the fallback
  data_outbox_path     TEXT NULL,          -- A: per-caller proxy data outbox dir   (NULL → use 'default' row)
  manifest_outbox_path TEXT NULL,          -- A: per-caller proxy manifest outbox dir
  sla_minutes          INT NOT NULL DEFAULT 30   -- job-level SLA (A: §3.7).  Independent of Network B's table.
  -- priority         INT NULL  -- DEFERRED: RabbitMQ 3.13 quorum has no message priority (QUESTIONS §21); reserved column.
);
```

> Resolution: look up the exact `calling_system_id`; if absent (or the needed column is NULL), fall back to the `'default'` row. SLA is **not** carried A→B — Network B has its own `calling_system_config` (below); the operator keeps the two aligned per `callingSystemId`.

---

### `conversion_rule`  (Network A only)

The per-`(callingSystemId, source_format)` processing rule the client specified — forward conversion, reverse conversion (carried to B via the mini-manifest), and the Split chunk-size limit. Network B has **no** equivalent table: it obeys the `reverse_conversion` value carried in the mini-manifest.

```sql
conversion_rule (
  calling_system_id   TEXT NOT NULL,        -- 'default' = fallback calling system
  source_format       TEXT NOT NULL,        -- matches source_file.original_format; 'default' = any format
  required_conversion TEXT NULL,            -- forward target → source_file.applied_conversion; NULL = pass-through
  reverse_conversion  TEXT NULL,            -- reverse target carried to NB in mini-manifest; NULL = NB skips reverse
  file_size_limit_mb  INT NULL,             -- Split.Worker max bytes per chunk → source_file.file_size_limit_mb
  PRIMARY KEY (calling_system_id, source_format)
);
```

> Resolution precedence (most specific wins): `(callingSystemId, sourceFormat)` → `(callingSystemId, 'default')` → `('default', sourceFormat)` → `('default', 'default')`. Convert.Worker resolves once and **snapshots** the three derived values onto the `source_file`, freezing the rule for the life of the job (mid-job config changes don't split a job's forward/reverse/sizing).

---

### `phase_config`

```sql
phase_config (
  phase                TEXT PRIMARY KEY,    -- 'Prepare' | 'Convert' | 'Split' | 'ProxyListener' | 'Assembly' | 'ReverseConvert' | 'Report'
  max_retries          INT NOT NULL DEFAULT 5
);
```

Same table on both networks; rows differ per phase. No heartbeat/sweeper-threshold columns — recovery is broker-level (`consumer_timeout`), not per-row sweeping. Kept as the home for a future per-phase escape-hatch heartbeat if metrics ever demand one for a single long phase (ARCHITECTURE.md §3.3).

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
  status                TEXT,              -- Created | Prepared | AllManifestsWritten | Failed  (done-states only)
  created_at            TIMESTAMPTZ,
  updated_at            TIMESTAMPTZ
);
CREATE INDEX job_status_active ON job(status) WHERE status NOT IN ('AllManifestsWritten','Failed');
```

---

### `source_file`

```sql
source_file (
  id                     UUID PRIMARY KEY,
  job_id                 UUID REFERENCES job(id),
  original_relative_path TEXT,             -- archive-container encoding baked here at Prepare: a container `name.ext`
                                           -- becomes directory `name_ext` (last dot → underscore), e.g.
                                           -- `parent.zip/inner.zip/leaf.docx` → `parent_zip/inner_zip/leaf.docx`;
                                           -- the leaf keeps its real extension. Applies to all supported archive types.
  original_format        TEXT,
  applied_conversion     TEXT NULL,        -- forward conversion applied (conversion_rule.required_conversion); NULL = pass-through
  converted_relative_path TEXT NULL,       -- 1→1 converter output (Q11); = original_relative_path for pass-through
  reverse_conversion     TEXT NULL,        -- snapshotted from conversion_rule at Convert; carried to NB in mini-manifest; NULL = NB skips reverse
  file_size_limit_mb     INT NULL,         -- snapshotted from conversion_rule at Convert; Split chunk-size cap (max bytes/chunk)
  status                 TEXT,             -- Pending | Converted | Split | Failed | NotSupported  (done-states only)
  failure_reason         TEXT NULL,        -- populated on terminal Failed/NotSupported; carried into manifest
  UNIQUE (job_id, original_relative_path)
);

-- Done-state CAS only. The Converted→Split (and terminal-Failed) transition is the exactly-once gate
-- for the job.files_manifest_written_count increment — no manifest_written flag, no heartbeat columns.
-- A redelivery / concurrent duplicate re-writes the identical mini-manifest but loses the CAS, so it
-- does not double-count. Mini-manifest is written to the proxy outbox BEFORE the commit (ARCHITECTURE.md §3.4 step 5).
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

### `calling_system_config`  (Network B variant)

Per-`callingSystemId` SLA/timeout with a `default` fallback. Network B holds **no** `conversion_rule` table — the reverse-conversion instruction arrives per file in the mini-manifest (`expected_file.reverse_conversion`).

```sql
calling_system_config (
  calling_system_id      TEXT PRIMARY KEY,   -- literal 'default' row is the fallback
  sla_minutes            INT NOT NULL DEFAULT 30,   -- job-level SLA (§5.7); independent of Network A's table
  report_pending_minutes INT NOT NULL DEFAULT 5     -- ReportPendingSweeper stuck-row threshold (§5.6)
  -- priority           INT NULL  -- DEFERRED (QUESTIONS §21); reserved column.
);
```

> Resolved by `callingSystemId` (from the job header) → `'default'` fallback. Operator keeps `sla_minutes` aligned with Network A's `calling_system_config` for the same `callingSystemId`.

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
                                           -- Assembling/ReportPending are counter/sweeper-driven lifecycle states, not per-worker claims
  callback_sent_at      TIMESTAMPTZ NULL,
  created_at            TIMESTAMPTZ DEFAULT now(),
  updated_at            TIMESTAMPTZ NULL,   -- bumped on every status transition (Reporter §5.6, TimeoutSweeper §5.7)
  cleaned_at            TIMESTAMPTZ NULL    -- set by Reporter after terminal-job artifact deletion (§6.6); NULL = not yet cleaned
);
CREATE INDEX job_active ON job(status) WHERE status IN ('Awaiting','Assembling','ReportPending');
CREATE INDEX job_cleanup ON job(updated_at) WHERE cleaned_at IS NULL;
```

---

### `inbox`

```sql
inbox (
  file_path   TEXT PRIMARY KEY,           -- absolute path in proxy delivery dir; unique; PK dedups proxy redeliveries.
                                           -- Deterministic re-split makes this first-writer-wins (ON CONFLICT DO NOTHING):
                                           -- a redelivery produces byte-identical chunks, so the duplicate is safely ignored.
                                           -- Chunks are NEVER copied — Assembly reads bytes in-place from this path during concat.
                                           -- After the assembling COMMIT, Assembly best-effort deletes the chunk files it
                                           -- just consumed (failures ignored); the proxy self-cleans days-old leftovers as backstop.
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
  original_relative_path TEXT,            -- carries Network A's archive-container encoding verbatim: a container `name.ext`
                                          -- appears as directory `name_ext` (last dot → underscore), e.g.
                                          -- `parent.zip/inner.zip/leaf.docx` → `parent_zip/inner_zip/leaf.docx`; leaf keeps its real extension.
  original_format        TEXT,
  applied_conversion     TEXT NULL,       -- forward conversion A applied (from mini-manifest)
  reverse_conversion     TEXT NULL,       -- reverse target from mini-manifest; ReverseConverter obeys it; NULL = pass-through (no reverse)
  expected_chunk_count   INT,             -- 0 for files marked Failed/NotSupported at manifest time
  received_chunk_count   INT DEFAULT 0,
  bytes_total            BIGINT,
  status                 TEXT,            -- Pending | Assembled | Finalized | Failed | NotSupported  (done-states only)
  failure_reason         TEXT NULL,       -- carried from manifest for files marked Failed/NotSupported by A
  UNIQUE (job_id, original_relative_path)
);
-- Done-state CAS only (Pending→Assembled by Assembly; Assembled→Finalized by ReverseConverter; →terminal by Reporter).
-- No intermediate ReverseConverting state, no heartbeat columns.
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

> Chunk processing is atomic (single CAS + counter increment + concat-if-last). Chunk bytes are NEVER copied — Assembly reads them in-place from `proxy_delivery_dir/<name>` during concat. Immediately after the assembling COMMIT, Assembly best-effort deletes the consumed chunk files (per assembled file; failures ignored). No long-running worker ownership on this row — no heartbeat columns.

---

### `outbox`

Same structure as Network A `outbox`.

---

## Pattern summary

| Pattern | Tables |
|---------|--------|
| Done-state CAS (`WHERE status='Prev'`; no `-ing` states, no heartbeat columns) | A: `job`, `source_file` · B: `job`, `expected_file` |
| Transactional outbox (`BIGSERIAL` id for FIFO) | `outbox` — both networks |
| Per-phase tuning | `phase_config` — both networks (`max_retries` default 5) |
| Per-caller config (default fallback) | A: `conversion_rule` + `calling_system_config` · B: `calling_system_config`. Reverse-conversion instruction carried A→B in the mini-manifest. |
| Counter increment (CAS-gated, same tx) | A: `job.files_manifest_written_count` · B: `job.finalized_file_count`, `expected_file.received_chunk_count` |
| UNIQUE + `ON CONFLICT DO NOTHING` | `source_file`, `chunk`, `expected_file`, `expected_chunk` |
| PK dedup | `inbox.file_path` (proxy redeliveries) — first-writer-wins; deterministic re-split yields byte-identical chunks so duplicates are safely ignored |
| Chunk-before-manifest buffer | `inbox.published=FALSE` — chunk before file-manifest, file-manifest before job header, 2-level buffer, drained inline. 3-level drain on job header handler (risk accepted). |
| Failure propagation A→B | via per-file mini-manifest (`{job_id}_{source_file_id}.file.json`): `source_file.failure_reason` → mini-manifest `status`/`failureReason` → `expected_file.failure_reason` |
| Chunks read in-place | `expected_chunk.name` resolves against proxy delivery dir; no work-dir copy; consumed chunks best-effort deleted post-COMMIT by Assembly |

## Resolved decisions

- **No `processed_message` table.** `inbox.file_path PRIMARY KEY` provides all dedup needed. `uuid_v5(filePath)` was only relevant if `inbox` were partitioned (cross-partition PK dedup gap). Partitioning deferred to year 2; until then the PK alone is sufficient.
- **No InboxSweeper.** Transactional `outbox` table + relay covers post-COMMIT crash recovery. Chunk-before-manifest case handled by `inbox.published=FALSE` drained inline on manifest arrival (see Assembly.Worker manifest path).
- **`outbox.id BIGSERIAL`** — relay reads with `ORDER BY id` for FIFO. UUID id would break ordering.
- **`phase_config` dedicated table** (not key/value rows in `config`) — typed columns; now holds only `max_retries` (heartbeat/sweeper columns removed).
- **No heartbeat columns anywhere** (`worker_id` / `last_heartbeat_at` / `*_stale_hb` indexes removed from all tables) — done-state CAS + broker redelivery + `consumer_timeout` replace per-row liveness tracking (ARCHITECTURE.md §3.3). Processing-worker identity, when needed, comes from logs/traces.
- **`failure_reason TEXT NULL`** on `source_file` + `expected_file` — drives the manifest-carries-failure flow (A→B) and the `StatusCallbackPayload` (B→client).
- **Chunk read in-place + post-COMMIT delete.** Assembly reads chunk bytes in-place from the proxy delivery dir during concat (never copied to a work dir) and, immediately after the assembling COMMIT, best-effort deletes the chunks it just consumed (per assembled file; failures ignored). Reporter sweeps any remaining job files on terminal `job.status`; the proxy self-cleans days-old leftovers as a backstop.
- **Archive-container path encoding (`name_ext`).** A nested-archive container `name.ext` is encoded as directory `name_ext` (last dot → underscore), e.g. `parent.zip/inner.zip/leaf.docx` → `parent_zip/inner_zip/leaf.docx` (leaf keeps its real extension). Baked into `original_relative_path` at Network A (Prepare) for all supported archive types; Network B uses it verbatim.
- **First-writer-wins dedup via determinism.** `inbox.file_path` PK + `ON CONFLICT DO NOTHING` is first-writer-wins: because converters/splitters are deterministic, a re-split after crash/timeout yields byte-identical chunks, so a duplicate proxy delivery is safely ignored (no overwrite / last-writer-wins).
- **No ManifestSweeper.** Per-file mini-manifests eliminate single "last writer" coordination.
- **No `manifest_written` flag.** The `source_file.status` done-CAS (`Converted→Split` / terminal-`Failed`) is itself the exactly-once gate for the `files_manifest_written_count` increment — a redelivery or concurrent duplicate loses the CAS and does not double-count.
- **Per-`callingSystemId` config with `default` fallback.** Caller-varying settings live in `conversion_rule` (A only: `(callingSystemId, source_format)` → forward/reverse conversion + split size limit) and `calling_system_config` (both networks: folders + SLA). Resolution precedence in ARCHITECTURE.md §6.1. Convert.Worker snapshots the resolved rule onto `source_file` (frozen for the job).
- **Reverse conversion carried via mini-manifest, not derived on B.** `reverse_conversion` is an independent per-`(callingSystemId, source_format)` target resolved on A (may be NULL=skip, equal the source format, or a different type). It is written to the mini-manifest and persisted on `expected_file`; Network B keeps **no** conversion config. Replaces the old "reverse = inverse of `applied_conversion`" rule.
- **Job priority deferred.** RabbitMQ 3.13 quorum queues have no native message priority (4.0 only); priority is out of scope pending a 4.x upgrade (QUESTIONS §21). `calling_system_config` reserves a `priority` column on both networks.
