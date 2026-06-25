# Dintinct — New Architecture (v3, Temporal-free)

> Cross-network file processing pipeline: **Network A** decomposes packages → **Proxy** (closed, third-party) ships files → **Network B** assembles. Designed for ~1M jobs/month, horizontal scale, crash resilience, no orchestrator framework.
>
> **Stack (pinned):** **RabbitMQ 4.3** with **quorum queues** ([docs](https://www.rabbitmq.com/docs/quorum-queues)), **PostgreSQL 16** ([docs](https://www.postgresql.org/docs/16/index.html)), .NET 9 workers on OpenShift. RabbitMQ runs as a **3-node cluster per network** (odd-sized for Raft majority — the minimum that gives quorum its replication/HA guarantee; a single node is a broker SPOF, see [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §20). RabbitMQ 4.x quorum queues add **native message priority** (32 strict levels, 0–31) — the basis for per-`callingSystemId` job priority (§6.1, §21).
>
> This document is aligned with the following diagrams and is authoritative together with them:
> - [`architecture-v3.drawio`](architecture-v3.drawio) — service topology
> - [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) — normal flow, crash recovery (broker redelivery), post-COMMIT crash (outbox), and deadlock recovery (client-side watchdog + `TimeoutSweeper-A` backstop, page 4)
> - [`chunk-flow.drawio`](chunk-flow.drawio) — 5-branch ProxyListener flow: chunk/job-header/file-manifest buffering, inline drains, ERROR sentinels
> - [`worker-exception-handling.drawio`](worker-exception-handling.drawio) — transient vs permanent failure paths

---

## 1. Guiding Principles

1. **No orchestration framework.** Replace Temporal with explicit, idempotent step-services coordinated by RabbitMQ + PostgreSQL.
2. **Per-network isolation.** Each side has its own Postgres and RabbitMQ; A and B never share infra or talk directly. The Proxy is the only bridge — over filesystem (data + manifest dirs) and the proxy's RabbitMQ message.
3. **Crash-safe via per-row status CAS + broker redelivery.** Idempotency rests on one primitive: every step ends with a single done-state transition `UPDATE row SET status='Done' WHERE id=$1 AND status='Prev' RETURNING id` — only the CAS winner publishes the next step; redeliveries and concurrent duplicate work are no-ops. There are **no intermediate `-ing` states, no row-heartbeat loop, and no per-phase sweeper.** Recovery is the message broker's job: an unacked message is automatically requeued when the consumer's channel/connection closes (crash → seconds; network death → ~60s via the AMQP connection heartbeat; app deadlock → up to RabbitMQ `consumer_timeout`). See §3.3 for the full model and calibration.
4. **Transactional outbox for next-step publishes — both A and B.** The DB commit and the downstream Rabbit publish are decoupled via an `outbox` table. The worker inserts the next-step message into `outbox` in the same transaction as the state flip; a separate `OutboxRelay` polls and publishes with publisher-confirm, marking `published_at=now()`. This closes the "COMMIT succeeded, crash before publish" gap.
5. **Per-file batch on NetworkB, chunks read in-place.** Each file finalizes the moment all its chunks are present — no global package wait. Chunks arriving before their manifest are buffered in `inbox` with `published=FALSE`; when the manifest arrives, Assembly drains the buffered rows inline. **Chunk bytes are NEVER copied to a work dir** — Assembly reads chunks directly from the proxy delivery dir during concat. Immediately after the assembling COMMIT, Assembly best-effort deletes the chunk files it just consumed from the proxy delivery dir (per assembled file), freeing disk incrementally rather than at job end; delete failures are ignored (Reporter sweeps any remainder on terminal job, and the proxy self-cleans days-old leftovers as a final backstop).
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

**Converter engine grouping (§3.1, §5.1, §10 #23):** the `Convert` box is one codebase deployed as **3 engine-grouped workers**, each with its own queue and `consumer_timeout` — `files.convert.image` (ImageMagick), `files.convert.doc` (Aspose), `files.convert.video` (ffmpeg). Network B's reverse step mirrors this as **2** workers (`files.reverse.image`, `files.reverse.doc`; no video — all A/V deliver as-is). Files that need no engine (pass-through + rename-to-TXT, per the client's File Conversion Table) carry `converter_group = NULL` and **skip the converter entirely**: Prepare routes them straight to `files.split` on A, and Assembly writes the assembled bytes straight to `target_path` on B.

---

## 3. Network A — Decomposition Side

### 3.1 Services

| # | Service | Responsibility |
|---|---------|----------------|
| 1 | **Ingestion.Api** | HTTP `POST /api/v1/ingestion`. Validates `IngestionRequestPayload`, inserts `job` row (status=`Created`), inserts `outbox(queue='jobs.created', payload)`. Returns `jobId`. |
| 2 | **Ingestion.FolderWatcher** | Polls watched directories; on stable file, builds payload, same insert+outbox as API. |
| 3 | **Ingestion.RabbitBridge** | Consumes external Rabbit queue carrying `IngestionRequestPayload`; same insert+outbox as API. |
| 4 | **Ingestion.RequestFileWatcher** | Watches request-file drop folder; reads JSON, same insert+outbox as API. |
| 5 | **Prepare.Worker** | Consumes `jobs.created`. Extracts archives recursively, lists files, records `source_file` rows per file. **Resolves the per-`callingSystemId` `conversion_rule` (default-fallback resolution, §6.1) for each file and snapshots `applied_conversion` / `reverse_conversion` / `file_size_limit_mb` / `converter_group` onto the `source_file`** — the snapshot moves here from Convert so the rule is frozen once (stable even if config changes mid-job) *and* pass-through files that skip Convert still carry their Split sizing + reverse instruction. **Routes each file's `outbox` row by `converter_group`:** an engine group → `outbox(queue='files.convert.{image\|doc\|video}')`; `NULL` (pass-through/rename) → `outbox(queue='files.split')` directly, bypassing Convert. Final done-CAS `job.status Created→Prepared` (gates the outbox inserts). After COMMIT, writes `{job_id}.job.json` (job header) to the manifest outbox dir via safe-write protocol (§3.6). |
| 6 | **Convert.Worker** (3 deployments: `image` / `doc` / `video`) | **One codebase, deployed once per engine group** (`CONVERTER_GROUP` env + a per-engine container image carrying only that engine's native deps), each consuming **only** its queue: `files.convert.image` (ImageMagick), `files.convert.doc` (Aspose), `files.convert.video` (ffmpeg). Reads the `applied_conversion` target Prepare snapshotted, runs `IFileConverter` (1→1) for it, sets `converted_relative_path` on the `source_file`. Done-CAS `source_file.status Pending→Converted`, inserts one `outbox(queue='files.split', payload)`. Each queue gets its **own `consumer_timeout` + watchdog** (§3.3) — image is sub-second, video runs minutes on multi-GB files. Pass-through/rename files (`converter_group=NULL`) never reach here. |
| 7 | **Split.Worker** | Consumes `files.split` (from a Convert worker, or directly from Prepare for pass-through/rename files). Splits into chunks sized by the `source_file`'s snapshotted `file_size_limit_mb` (max bytes per chunk; from the per-`callingSystemId` `conversion_rule`), records `chunk` rows (`ON CONFLICT DO NOTHING`), writes chunk bytes to data outbox dir. Writes `{job_id}_{source_file_id}.file.json` (per-file mini-manifest) to manifest outbox dir via safe-write protocol — one per source file, written BEFORE the DB commit (or on permanent failure, same write-before-commit). Then done-CAS `source_file.status Converted→Split` (or `Failed`/`NotSupported`); the same CAS-winning tx increments `job.files_manifest_written_count`; when the counter reaches `total_source_files`, flips `job.status='AllManifestsWritten'`. The status CAS is itself the exactly-once gate for the counter (a redelivery or concurrent duplicate hits `WHERE status='Converted'` and no-ops). No single end-of-job manifest; no ManifestSweeper; no `manifest_written` flag. |
| 8 | **OutboxRelay-A** | Polls `outbox WHERE published_at IS NULL` every ~500ms (`FOR UPDATE SKIP LOCKED`), publishes to RabbitMQ with publisher-confirm (setting the AMQP message `priority` from `outbox.priority`, §6.1), updates `outbox.published_at`. |

> **Open trade-off on ingestion services:** the user chose 4 separate ingestion services (one per channel) for crash isolation. Reviewers argued for a single `Ingestion` service hosting all 4 channels as in-process adapters. Recorded in [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §7.

### 3.2 Postgres-A Schema

Full DDL is canonical in [`DB-SCHEMA.md` → Network A](DB-SCHEMA.md#network-a--postgres-a). Tables: `job`, `source_file`, `chunk`, `nested_archive`, `outbox`, `phase_config`, `config`, `calling_system_config`, `conversion_rule`. (`calling_system_config` + `conversion_rule` hold the per-`callingSystemId` configuration with default fallback — §6.1.) (`converted_file` removed — the converter is 1→1, Q11, so its output folds into `source_file.applied_conversion` + `converted_relative_path`.) State vocabularies referenced below:

- `job.status` — `Created | Prepared | AllManifestsWritten | Failed` (done-states only).
- `source_file.status` — `Pending | Converted | Split | Failed | NotSupported` (done-states only).
- `job.files_manifest_written_count` — atomic counter; reaching `total_source_files` is the sole `AllManifestsWritten` gate.

`max_retries=5` applies to every phase, tunable per phase via `phase_config`. No per-row heartbeat or sweeper-threshold columns: liveness/recovery is RabbitMQ redelivery + `consumer_timeout` (§3.3), not row sweeping. `phase_config` is the home for any future per-phase override (the §3.3 escape-hatch heartbeat).

### 3.3 Step contract (applies to every A-worker and every B-worker)

This is the canonical worker loop. Diagram 1 of [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) is the visual reference.

```
on inbound message:
  short-circuit (cheap pre-check, best-effort): if entity.status is already at/past
     this step's done-state, or job terminal → ack + return   (redelivery / already done)

  do business work IDEMPOTENTLY (outside or before the tx — file IO is not transactional):
     - write deterministically-named files (.tmp+rename — locally and to the proxy outbox; the proxy ignores .tmp)
     - INSERT child rows ON CONFLICT DO NOTHING
     - (for Split: write the per-file mini-manifest to the proxy outbox dir HERE, before the commit)

  BEGIN tx
    done-CAS the row (single transition, no intermediate -ing state):
      UPDATE entity
         SET status='Done'
       WHERE id=$1 AND status='Prev'
       RETURNING id
    IF CAS returned no row → COMMIT + ack + return   (another worker won; our work was redundant, harmless)
    increment any per-step counter in THIS tx (gated by the CAS above)
    INSERT INTO outbox(queue, payload, priority=job.priority, published_at=NULL) for the next-step message(s)
  COMMIT tx
  ack inbound
```

**No row-heartbeat loop, no intermediate `-ing` state, no per-phase sweeper.** A step "claims" nothing up front — it does its idempotent work, then races to flip the done-state. The done-CAS is the single serialization point: exactly one worker flips `Prev→Done`, and only that worker's counter increment and outbox inserts take effect. A redelivery or a concurrent duplicate that lost the race finds `status='Done'` (or the short-circuit) and no-ops. Because the message is acked only after COMMIT, an unacked message always reflects unfinished work and is safe to redeliver.

**OutboxRelay** (one process pair per network, 2–3 replicas competing) runs continuously:

```
loop every ~500ms:
  SELECT id, queue, payload, priority
    FROM outbox
    WHERE published_at IS NULL
    ORDER BY id
    FOR UPDATE SKIP LOCKED
    LIMIT 100
  for each row:
    rabbit.publish(queue, payload, priority=row.priority, confirm=sync)  -- AMQP priority property; quorum honors it natively (§6.1)
    UPDATE outbox SET published_at=now() WHERE id=$1
```

**Crash scenarios (recovery is the broker's job — no sweeper):**

- **Crash before COMMIT** → DB unchanged; any file work was idempotent (deterministic names). The message was never acked, so RabbitMQ requeues it the moment the channel/connection closes; a new worker reprocesses from scratch. The done-state was never reached, so there is no stuck row to reset.
- **Crash after COMMIT, before ack** → message redelivers; the new worker hits the short-circuit / a failing CAS (`status='Done'`) and acks. The outbox row already exists → OutboxRelay publishes it (~500ms).
- **Worker hung/alive (deadlock, connection still up)** → the message stays unacked but the broker sees a healthy connection, so it waits for `consumer_timeout`, then closes the channel and requeues. Recovery latency = `consumer_timeout` (tunable; see calibration).

**Failure-mode → recovery latency (RabbitMQ at-least-once; consumers are idempotent by the CAS above):**

| Failure mode | How RabbitMQ detects it | Recovery latency |
|--------------|-------------------------|------------------|
| Process crash / OOM-kill / pod evict / SIGTERM redeploy | OS closes the TCP socket → channel/connection close → unacked deliveries requeued | **seconds** |
| Hard kill / network partition (no clean close) | AMQP **connection heartbeat** (default 60s, dead after 2 missed) closes the connection → requeue | **~60s** |
| App deadlock / infinite loop (connection still alive) | **`consumer_timeout`** — broker closes the channel if a delivery is unacked past the timeout, requeuing all of that channel's deliveries | **up to `consumer_timeout`** (default 30 min) |

> Note: the "connection heartbeat" above is RabbitMQ's transport-level keepalive, **not** the removed per-row DB heartbeat. They are unrelated.

**Why this is reliable without a sweeper:** the only case the old per-phase sweeper uniquely accelerated was the deadlock row (last table row) — and even then it only reduced latency, never correctness. Crash and network-death (the common cases) recover *faster* here than under the old design, because there is no `-ing` row to block the redelivery from reprocessing. The deadlock case is bounded by `consumer_timeout` and backstopped by the job-level `TimeoutSweeper` (§5.7, Network B) and, on Network A, by per-file DLQ-terminal + the `files_manifest_written_count` counter (a job always reaches `AllManifestsWritten` once every file is `Split`/`Failed`/`NotSupported`).

**Queue type & the client-side watchdog:**

> **Queue type: quorum (decided).** RabbitMQ 4.3, quorum queues, 3-node cluster per network. Quorum gives the three broker-native backstops this design relies on — `consumer_timeout`, `delivery-limit`, and at-least-once dead-lettering — plus, on 4.x, **native message priority** (§6.1), and replicates every queue across the 3 nodes (durable by default, survives one node loss). Per-queue Raft cost is negligible at this scale (~4 jobs/sec, small fixed queue set). Classic queue mirroring was removed in 4.0 and is not used here; a single-node broker is a SPOF and is called out in [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §20.
>
> **Client-side watchdog (still deployed — it reclaims faster than `consumer_timeout`).** `consumer_timeout` is broker-side enforcement of "this delivery has been unacked too long," evaluated at 1-minute intervals with a 30-min default — coarse. A worker-local cancellation timer reclaims a hung-but-alive worker far sooner, with no DB state:
>
> ```csharp
> using var cts = new CancellationTokenSource(maxStepDuration);  // ≈3–4× p99 for this phase
> await ProcessMessageAsync(delivery, cts.Token);   // hung step self-cancels → throw → nack → channel close → requeue
> ```
>
> This recovers a hung worker in **~`maxStepDuration` (seconds–single minutes)** instead of waiting the full `consumer_timeout`. The watchdog timeout throws like any transient failure, so it feeds the app-level `max_retries=5` counter (nack → retry queue → `x-death`); the quorum `delivery-limit` (set explicitly — see calibration #3) is the broker-side poison backstop behind it. `maxStepDuration` MUST exceed the longest legitimate step for that phase, same bound as `consumer_timeout` — too low kills healthy slow work and false-dead-letters it.

**Calibration (set these from metrics; defaults are conservative):**

1. **`consumer_timeout`** (RabbitMQ, node-level; enforced on quorum queues — default **30 min**). The broker-native deadlock backstop; the **client-side watchdog (above) reclaims faster**, so `consumer_timeout` is the backstop, not the primary defense — set it generously. Tune to ≈ **3–4× the measured max single-message processing time** of the slowest phase, with a **minimum of 5 minutes** (the broker evaluates timeouts at 1-minute intervals; values below 5 minutes are discouraged by the RabbitMQ docs). It MUST exceed the longest legitimate step — otherwise healthy work is killed mid-flight, requeued, and (via the `delivery-limit`) eventually false-dead-lettered. Start at the 30-min default and tune down once you have data. **Per engine-group queue (§3.1/§5.1):** `files.convert.{image,doc,video}` and `files.reverse.{image,doc}` are calibrated **independently** — this is the main reason the converter is split by engine. A single shared `Convert` timeout would have to cover the ffmpeg worst case (a 6 GB transcode runs minutes), leaving a hung sub-second ImageMagick conversion unreclaimed for that whole window; separate queues let each carry its own bound. With `prefetch=1`, a long video also never head-of-line-blocks a waiting image.
2. **`prefetch = 1`** (`basic.qos`) per worker channel. On a `consumer_timeout` the broker closes the channel and requeues **all** in-flight deliveries on it; `prefetch=1` caps the blast radius to a single message.
3. **`delivery-limit`** — **defaults to 20 on RabbitMQ 4.x**; we keep it at **20** and still set it **explicitly via policy** for clarity and to pin it across version changes. It is the poison-message backstop: a `consumer_timeout` channel-close counts as a genuine failure toward the limit, so a perpetually-stuck message is eventually dropped/dead-lettered rather than looping forever. The app-level `max_retries=5` counter (`x-death`, fed by watchdog-timeout nacks) is the parallel app-side poison backstop — both are in effect.
4. **Measure first.** RED metrics already capture per-step duration (§6.3). After ~1 week of real traffic, read p99/max per phase and set `consumer_timeout` accordingly. Surface the two driving numbers to the client — max single-file step time and acceptable deadlock-recovery latency — in [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §19.
5. **Escape hatch (only if metrics demand it):** if one phase is BOTH long *and* needs reclaim faster than its `consumer_timeout`, re-introduce a per-row heartbeat + a single-replica sweeper for **that phase only** (this is what `phase_config` is reserved for). Do not bring the layer back globally.
6. **At-least-once dead-lettering (mandatory):** by default RabbitMQ dead-letters messages *without* publisher confirms (`dead-letter-strategy = at-most-once`) — a full or unavailable DLX target silently drops the message with no error. Apply **`dead-letter-strategy = at-least-once`** as a **policy on all quorum queues** (main + retry). On RabbitMQ 4.x this strategy requires the queue also use **`overflow = reject-publish`** (it does **not** work with the default `drop-head`) plus a configured dead-letter-exchange — both already set by this topology (§3.5). The broker then uses publisher confirms internally when routing to the dead-letter target, closing the silent-loss gap. Without this, a transient failure whose `*.retry` queue is full permanently loses the message — the file never reaches `Failed`, the counter never increments, and Network B hangs until `TimeoutSweeper` fires 30 minutes later (Network A: until the §3.7 `TimeoutSweeper` fires).

**Publisher confirms are non-negotiable inside the OutboxRelay.** Without sync confirms, `published_at=now()` could be set on a message Rabbit dropped.

### 3.4 Decomposition flow (precise)

1. **Ingest** → insert `job(status=Created)` + `outbox(queue='jobs.created', payload={job_id})`. COMMIT, return jobId.
2. **Prepare** consumes `jobs.created`. Extracts archives into work dir. For every leaf file: insert `source_file(status='Pending')` (`ON CONFLICT DO NOTHING`), **resolve the `conversion_rule` for `(job.calling_system_id, original_format)` with default fallback (§6.1) and snapshot `applied_conversion` / `reverse_conversion` / `file_size_limit_mb` / `converter_group` onto the row** (frozen once here; pass-through files that skip Convert still carry Split sizing + the reverse instruction). Sets `total_source_files`. Done-CAS `job.status Created→Prepared` — the CAS-winning tx inserts one `outbox` row per file, **routed by `converter_group`:** engine group → `queue='files.convert.{image|doc|video}'`; `NULL` (pass-through/rename) → `queue='files.split'` (Convert is bypassed). **After COMMIT**, writes `{job_id}.job.json` to the **manifest outbox dir** via safe-write protocol. This job-header file lets NB create a `job` row and begin accepting file-manifests before any Split.Worker finishes.
3. **Convert** (engine-grouped — 3 deployments of one codebase, each consuming only `files.convert.image` | `.doc` | `.video`; §3.1) consumes each message on its queue. Runs the converter (1→1) for the `applied_conversion` target Prepare snapshotted (ImageMagick / Aspose / ffmpeg respectively); sets `converted_relative_path` on the `source_file`. Done-CAS `source_file.status Pending→Converted`. Inserts one `outbox(queue='files.split')`. **Pass-through/rename files do not pass through this step** — Prepare routed them straight to `files.split`, so they are already `Pending` with `applied_conversion=NULL` (or a TXT relabel for the rename group; see §6.1) and go directly to step 4.

   > **Determinism requirement (assumed — BLOCKERS S5):** the converter must be deterministic, and this is the current **working assumption** for all converters (pending client confirmation). It matters more in the done-states model: a `consumer_timeout` requeue can run a second worker on the same file concurrently with a slow-but-healthy first worker. A byte-identical re-split is what makes first-writer-wins chunk dedup (`inbox.file_path` PK + `ON CONFLICT DO NOTHING` on Network B) safe — a redelivery re-produces the same chunk bytes under the same names. **Per-converter determinism sign-off is mandatory before each converter goes live.** Known carve-out: the PDF→DOCX (Aspose) converter is confirmed non-deterministic and must be made deterministic before it ships, with persist-and-re-serve of the first split output kept as a documented contingency (see [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §12).

4. **Split** consumes each `files.split` (from a Convert worker, or directly from Prepare for pass-through/rename files). Splits the file — the converted output, or the original bytes for pass-through — into chunks of at most `source_file.file_size_limit_mb` (snapshotted from the per-`callingSystemId` `conversion_rule` at Prepare; default fallback per §6.1), named `{job_id}_{source_file_id}_chunk_{index}{ext}` (unique because the converter is 1→1 — one converted file per source file, so `index` never collides). Writes bytes to **data outbox dir** via the safe-write protocol in §4.2. Inserts `chunk` rows (`source_file_id`, `ON CONFLICT DO NOTHING`). The done-CAS `Converted→Split` flip is NOT done here — it happens in step 5 after the mini-manifest is written, so the flip and the counter increment commit together.

5. **Split, same handler** — per-source-file completion + per-file mini-manifest:

   The worker writes the mini-manifest first, then commits the done-CAS `Split` flip:

   a. **Write mini-manifest first (crash-safety sequencing):** build `{job_id}_{source_file_id}.file.json` (deterministic JSON — see §3.6) and write it to the **manifest outbox dir** via safe-write protocol. This happens BEFORE the DB commit that sets terminal status. Rationale: the file write is not transactional, and NB depends on it. If the worker crashes after writing the file but before committing, the message redelivers, the retried worker re-splits (idempotent chunk names), re-writes the identical mini-manifest, and then commits — same final state. Committing `Split` *before* writing the file would be unsafe: a crash in between would short-circuit the redelivery (`status='Split'`) and the file would never be written, hanging NB until timeout.

   b. **Done-CAS gates the counter increment (no `manifest_written` flag):**

   ```sql
   -- The status guard alone makes this exactly-once: only the first Converted→Split wins.
   UPDATE source_file
      SET status = 'Split'
    WHERE id = $source_file_id
      AND status = 'Converted'
    RETURNING id;
   -- (only run the job counter update if RETURNING produced a row)

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

   The `WHERE status='Converted'` guard ensures exactly-once counting: a redelivery or a concurrent duplicate worker (e.g. after a `consumer_timeout` requeue) re-runs the idempotent split and re-writes the identical mini-manifest, but only the first `Converted→Split` transition succeeds, so the counter increments exactly once. No separate `manifest_written` flag is needed.

   When `files_manifest_written_count` reaches `total_source_files`, `job.status` flips to `AllManifestsWritten`. No ManifestSweeper is required.

   > **Permanent failure path:** on a permanent failure for a source file (either in this handler or via the DLQ consumer), the same write-before-commit sequencing applies: write the mini-manifest (with `status='Failed'`, `chunks=[]`) to the manifest outbox dir BEFORE committing `source_file.status='Failed'`. The terminal-status CAS (`WHERE status NOT IN ('Split','Failed','NotSupported')`) gates the counter increment exactly-once, the same way the `Converted→Split` CAS does on the success path.

### 3.5 Retry policy (NetworkA) — aligned with `worker-exception-handling.drawio`

On worker exception, the worker classifies the exception against a fixed whitelist:

| Class | Example exception types | Action |
|-------|--------------------------|--------|
| **Transient** | `SqlException` (transient subclass), `BrokerUnreachableException`, `IOException` (network-related), `TimeoutException`, `OperationCanceledException` (non-shutdown), Polly transient handlers | `nack(requeue=false)` → DLX → `*.retry` queue (1-min TTL) → redeliver with `x-retry-count++` |
| **Permanent** | `UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException`, deterministic application exceptions | `UPDATE source_file SET status='Failed', failure_reason=<message>` (A-side analog of B-side `expected_file`); `nack(requeue=false)` → `*.dead` |
| **Unknown** | Anything not in either list | Treat as **transient** by default. Retries cover spurious errors; if the loop persists, DLQ catches it after `max_retries`. |

- **`max_retries` default = 5.** After `x-retry-count >= 5`, the transient message lands in `*.dead`. **Both** kinds of dead-lettered message reach the same `*.dead` DLQ consumer: (a) transient-exhausted messages, and (b) permanent-failure messages `nack`ed straight to `*.dead` (table row above). The consumer is idempotent on `source_file.status`: if the file is not yet terminal it writes the per-file mini-manifest (with `status='Failed'`, `chunks=[]`) to the manifest outbox dir **before** committing, then done-CAS `source_file.status='Failed'` (`WHERE status NOT IN ('Split','Failed','NotSupported')`), which also gates the counter increment; if the file is already terminal (the inline permanent path of §3.4 step 5 already wrote the manifest) the CAS no-ops and the write is skipped. Both paths follow the same write-before-commit sequencing, so Network B always learns the file failed and never waits on it forever.
- **Job-level `Failed` is set ONLY by:** (i) Split's per-file counter reaching `total_source_files` when all source files are `Failed`/`NotSupported`, or (ii) external operator intervention. Workers never set `job.status='Failed'` directly mid-pipeline.
- **Backpressure:** every queue — main, retry, **and dead** — declared with `x-max-length` + `x-overflow=reject-publish`. When OutboxRelay's publish gets `basic.nack`, it leaves the outbox row unpublished and retries next poll. Stall propagates upstream as outbox tables grow (visible in metrics) and ultimately as `basic.publish` nacks at Ingestion.Api (HTTP 429). **`reject-publish-dlx` is never used** — on `*.dead` queues it would dead-letter the overflow message to an undefined further exchange and silently drop it. Instead, set `x-max-length` large on `*.dead` queues (e.g. 100 000) and alert on depth before the limit is reached.
- Tunable values (`x-max-length`, `x-message-ttl`) set via **policies**, not declaration args.

### 3.6 Per-file manifest schema

There are two distinct file types written to the manifest outbox dir:

**Job header** — `{job_id}.job.json` — written by Prepare.Worker after `Created→Prepared`:

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
  "appliedConversion": "...",          // forward conversion applied on A; null if pass-through
  "reverseConversion": "...",          // reverse target type for NB; null = NB skips reverse conversion (pass-through)
  "reverseConverterGroup": "...",      // NB reverse-engine routing: "image" | "doc" | null (null = no reverse engine → Assembly writes through). Derived on A from reverseConversion; B obeys it (B holds no conversion config)
  "status": "Split",                   // Split | Failed | NotSupported
  "failureReason": null,               // populated for Failed / NotSupported
  "chunks": [
    { "index": 0, "name": "...", "byteLength": 12345 }
  ]
}
```

For failed files: `status` is `"Failed"` or `"NotSupported"`, `failureReason` is set, and `chunks` is `[]`.

> `byteLength` is carried for audit/observability only. **Network B does NOT validate received chunk size against it** — assembly completion is count-based (`received_chunk_count == expected_chunk_count`) and concat proceeds regardless of any size difference.

`reverseConversion` is the **only** A→B carrier of the per-`callingSystemId` reverse-conversion instruction (§6.1): A resolves it from the `conversion_rule`, B obeys it verbatim and keeps no conversion config of its own. It is independent of `appliedConversion` — it can name a different target type, equal the original format, or be `null` to mean "no reverse conversion" (see §5.5).

NB's Assembly.Worker consuming `file-manifests.received` uses each mini-manifest to upsert one `expected_file` + its `expected_chunk` rows, persisting `appliedConversion` and `reverseConversion` onto the `expected_file` (ReverseConverter reads them later). If `status` is `Failed` or `NotSupported`, it creates `expected_file` in a terminal state with no `expected_chunk` rows — NB never waits for chunks that will never arrive. Reporter's terminal-count logic then sees those files as already terminal.

### 3.7 Network A `TimeoutSweeper` (job-level backstop)

> Added alongside the client-side watchdog (§3.3) as defense-in-depth — it is the job-level SLA backstop for the residual failure modes that the watchdog + quorum backstops do not fully cover. Visual: [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) **page 4** (watchdog reclaim ① + sweeper backstop ②).

Network A's job completion gate is `files_manifest_written_count` reaching `total_source_files` (→ `AllManifestsWritten`). That gate is only reached once **every** `source_file` is terminal (`Split`/`Failed`/`NotSupported`). The watchdog + DLQ-terminal path normally guarantee that. But two residual failure modes can strand a single `source_file` short of terminal forever, hanging the whole job:

- a watchdog-evading hang (e.g. the watchdog thread itself wedged), and
- a message wedged because its dead-letter target is persistently full/unavailable — at-least-once DLX (§3.3 #6) keeps it from being *lost*, but it stays unprocessed, so the file never reaches `Failed` and the counter never increments.

`TimeoutSweeper-A` is the job-level SLA backstop for both. It mirrors Network B's §5.7 sweeper — **job-grain, not per-row; no `-ing` states, no per-row heartbeat, no schema change** (uses existing `job.created_at`/`updated_at` and `source_file.status`). Runs every 60s on a single sweeper-enabled replica (`SWEEPER_ENABLED=true`); per-row CAS gates make accidental concurrent runs safe.

```sql
-- (i) find jobs past their per-callingSystemId SLA that never reached a terminal job state.
--     SLA minutes resolved from calling_system_config with default fallback (§6.1).
WITH t AS (
  SELECT j.id FROM job j
   LEFT JOIN calling_system_config csc ON csc.calling_system_id = j.calling_system_id
   LEFT JOIN calling_system_config def ON def.calling_system_id = 'default'
   WHERE j.status NOT IN ('AllManifestsWritten','Failed')
     AND now() - j.created_at
           > make_interval(mins => COALESCE(csc.sla_minutes, def.sla_minutes, 30))  -- A-side SLA; align with §5.7
   LIMIT 10 FOR UPDATE SKIP LOCKED
)
SELECT id FROM t;

-- (ii) for each stranded source_file of such a job, force it terminal so the
--      counter can complete. Write-before-commit sequencing as in §3.4 step 5:
--      write the mini-manifest ({status:'Failed', failureReason:'SweeperTimeout', chunks:[]})
--      to the manifest outbox dir BEFORE this commit, so NB always learns the file failed.
UPDATE source_file
   SET status = 'Failed',
       failure_reason = 'SweeperTimeout'
 WHERE job_id = $job_id
   AND status NOT IN ('Split','Failed','NotSupported')
 RETURNING id;                       -- $k = files forced this run

-- (iii) advance the counter by $k in the SAME tx; flips to AllManifestsWritten when it reaches total
UPDATE job
   SET files_manifest_written_count = files_manifest_written_count + $k,
       status = CASE WHEN files_manifest_written_count + $k = total_source_files
                     THEN 'AllManifestsWritten' ELSE status END,
       updated_at = now()
 WHERE id = $job_id;
COMMIT;
```

The terminal-status CAS (`WHERE status NOT IN (...)`) is the exactly-once gate, identical to the normal Split path — if a slow-but-healthy worker finishes a file concurrently, only one of {worker, sweeper} flips it and increments. After the sweep, the job reaches `AllManifestsWritten`, NB receives a `Failed` mini-manifest for each stranded file, and the job converges instead of hanging indefinitely.

**SLA = per-`callingSystemId`, default 30 minutes** (resolved from `calling_system_config` with `default` fallback, §6.1), measured from `job.created_at`. Keep the A-side and B-side SLA for a given `callingSystemId` aligned (§5.7) — they live in independent per-network `calling_system_config` tables, so the operator sets both — so a job times out coherently across both sides.

---

## 4. The Proxy

The Proxy is **third-party and unchanged**. It:

1. Watches the data outbox dir and manifest outbox dir on the A side.
2. Ships each file unmodified to corresponding dirs on the B side (referred to throughout this document as the **proxy delivery dir**).
3. On failure, drops a sentinel: `<original-name>.ERROR.txt` or `<original-name>.UNSUPPORTED.txt`.
4. Publishes a Rabbit message per transferred file, **only after the file has finished moving into the proxy delivery dir**:

   ```json
   { "filePath": "<absolute-path-on-B-side>" }
   ```

   NetworkB acts on this message, never on a filesystem-watch event, so it never observes a mid-write file — this message-after-move ordering is the truncation guard (see §5.9).

**This contract is frozen.** The proxy does NOT delete files from the proxy delivery dir on Network B as part of the normal flow — Network B owns those files from the moment they land there, and cleanup is Network B's responsibility (see §6.6). As a final backstop, the proxy self-cleans days-old leftover files from its delivery dir.

### 4.1 Partial-file pickup — resolved

The client confirmed the proxy **ignores `.tmp` files**, so the partial-file-pickup risk is closed: NetworkA writes each file to the proxy outbox as `<name>.tmp` then renames it to `<name>` on the same filesystem. The proxy only picks up the renamed final file, never the in-progress `.tmp`. This reverses the earlier "must NOT write `.tmp` to the proxy outbox / staging-subdir + mv" workaround — `.tmp`+rename is now the confirmed chosen mechanism. See [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §1.

### 4.2 Safe-write protocol

Every file write uses `.tmp + rename` on the same filesystem — write to `<name>.tmp`, then rename to `<name>` so consumers only ever see the complete file:

- Local working files (A work dir, B's working files, assembled output) — `.tmp + rename`.
- A → proxy outbox writes (both the data outbox and the manifest outbox dirs) — `.tmp + rename`; the proxy ignores `.tmp` files (§4.1), so it never picks up a partial file. No staging subdirectory and no separate `mv` step.
- B `target_path` and the NetworkB CSV — `.tmp + rename`; the consuming system scanning `target_path` ignores `.tmp` files (§5.5/§6.5).

---

## 5. Network B — Assembly Side

### 5.1 Services

| # | Service | Responsibility |
|---|---------|----------------|
| 1 | **ProxyListener** | Consumes proxy Rabbit. Two-stat synchronous stability check (§5.9). For each event, inserts `inbox` row + (conditionally) `outbox` row in a single transaction. Seven branches classified by filename pattern — see §5.3 and [`chunk-flow.drawio`](chunk-flow.drawio). Handles: job headers (`{job_id}.job.json`), per-file mini-manifests (`{job_id}_{source_file_id}.file.json`), chunks, and ERROR/UNSUPPORTED sentinels. **No file copy at any point** — chunks stay in the proxy delivery dir until Assembly reads them during concat. |
| 2 | **Assembly.Worker** | Consumes `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, and `chunks.received`. **Job-header path:** INSERT `job` row (`status='Awaiting'`, `expected_file_count`), drain buffered file-manifest and chunk inbox rows inline. **File-manifest path:** upsert one `expected_file` (terminal-Failed for failed files; Pending otherwise), persisting `applied_conversion` + `reverse_conversion` + `reverse_converter_group` from the mini-manifest, + its `expected_chunk` rows, drain buffered chunks for this file inline, run concat if complete. **File-manifest-error path:** create terminal `expected_file` with `status='Failed'`, emit `outbox(files.finalized)`. **Chunk path:** CAS `expected_chunk.received_at`, increment `expected_file.received_chunk_count`; if it flips to complete, run concat before COMMIT, then **route by `reverse_converter_group`** (§5.5): `image`/`doc` → CAS `Pending→Assembled` + `outbox(files.reverse.{group})` (a reverse worker finalizes); `NULL` (pass-through/rename) → write the assembled bytes straight to `target_path` via `.tmp+rename`, CAS `Pending→Finalized` (no reverse handoff, so `Assembled` is skipped) + `outbox(files.finalized)`. Immediately after COMMIT, best-effort deletes the consumed chunk files for this file from the proxy delivery dir (failures ignored). |
| 3 | **ReverseConverter.Worker** (2 deployments: `image` / `doc`) | **One codebase, deployed once per engine group** (`CONVERTER_GROUP` env + per-engine image), each consuming **only** its queue — `files.reverse.image` (ImageMagick) or `files.reverse.doc` (Aspose). No `video` deployment: every audio/video file delivers as-is (Target Format = "No conversion needed"), so it never reaches a reverse worker. Reads the assembled file from `work/{job_id}/assembled/{file_id}.bin`; applies `IFileConverter` from the assembled file's current format (`applied_conversion` if forward-converted, else `original_format`) **to** the `expected_file.reverse_conversion` target. Writes the result under `target_path` via `.tmp + rename`. Done-CAS `expected_file.status Assembled→Finalized` (no intermediate `ReverseConverting`). Writes `outbox(files.finalized)`. Pure transform — never touches the proxy delivery dir or per-chunk bytes. Pass-through files (`reverse_converter_group=NULL`) never reach here — Assembly wrote them through directly. |
| 4 | **Reporter.Worker** | Consumes `files.finalized`, `chunks.error`, `chunks.unsupported`. CAS `expected_file.status` to terminal; increments `job.finalized_file_count`; on completion flips `job.status='ReportPending'`, writes `{job_id}_report.csv`, dispatches `StatusCallbackPayload` via `IAnswerDispatcher`, final CAS to `Done`. Hosts `ReportPendingSweeper`, `TimeoutSweeper`, and per-phase heartbeat sweepers on a single sweeper-enabled replica. On terminal job, best-effort sweeps any files still remaining in the proxy delivery dir for the job (job-header, per-file manifests, sentinels, and chunks of Failed/partial files Assembly never consumed) — Assembly has already deleted the bulk of chunks post-COMMIT, and the proxy self-cleans any leftovers (see §6.6). |
| 5 | **OutboxRelay-B** | Polls `outbox WHERE published_at IS NULL` every ~500ms, publishes to RabbitMQ with publisher-confirm (setting the AMQP message `priority` from `outbox.priority`, §6.1), marks `published_at=now()`. 2–3 replicas competing via `FOR UPDATE SKIP LOCKED`. |

### 5.2 Postgres-B Schema

Full DDL is canonical in [`DB-SCHEMA.md` → Network B](DB-SCHEMA.md#network-b--postgres-b). Tables: `inbox`, `job`, `expected_file`, `expected_chunk`, `outbox`, `phase_config`, `config`, `calling_system_config`. (`calling_system_config` holds B's per-`callingSystemId` SLA/timeout with default fallback — §6.1; B keeps **no** conversion config, the reverse-conversion instruction arrives via the mini-manifest.) State vocabularies and the buffer flag the flow logic below relies on:

- `inbox.published` — `FALSE` = buffered (chunk before its file-manifest, or file-manifest before its job header); `TRUE` once a downstream queue message is emitted or an inline drain consumes the row. `inbox.file_path` PK dedups proxy redeliveries.
- `job.status` — `Awaiting | Assembling | ReportPending | Done | PartiallyDone | Failed | TimedOut` (`Assembling`/`ReportPending` are counter/sweeper-driven lifecycle states, not per-worker claims).
- `expected_file.status` — `Pending | Assembled | Finalized | Failed | NotSupported` (done-states only).
- `expected_chunk.name` — proxy filename, resolved against the proxy delivery dir at concat time. **Chunk bytes are never copied.**

Partitioning deferred to year 2.

### 5.3 ProxyListener branches and the chunk/manifest interaction

This section is the textual companion to [`chunk-flow.drawio`](chunk-flow.drawio). ProxyListener classifies each incoming proxy event by filename pattern and executes one of seven branches, all in a single transaction. **No file copies anywhere.**

> **Note on the post-concat emission (§10 #23):** the Assembly SQL below shows `INSERT outbox(queue='files.assembled')` for brevity. The actual queue is **routed by `expected_file.reverse_converter_group`** (§5.5): `image`/`doc` → `files.reverse.{group}` (a reverse worker finalizes); `NULL` (pass-through/rename) → Assembly instead writes the assembled bytes straight to `target_path`, flips `Pending→Finalized`, and emits `files.finalized`. Read `files.assembled` in the snippets as "the routed next-step message."

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
4. After COMMIT, best-effort delete the consumed chunk files for this file from the proxy delivery dir (resolve by `expected_chunk.name`); delete failures are ignored. This is post-COMMIT, so a crash before it just leaves the chunks for Reporter/proxy to sweep, and a redelivery finds the file already `Assembled` and no-ops.

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

**Routing by `reverse_converter_group` (mirrors A's engine split, §3.1):** after concat, Assembly routes the assembled file by the `reverse_converter_group` it persisted from the mini-manifest (`image` | `doc` | `null`). A reverse worker runs only for `image`/`doc`; pass-through (`null`) is written through by Assembly itself. So the three `reverse_conversion` cases split across two actors:
- **`reverse_conversion IS NULL` → no reverse conversion (pass-through), `reverse_converter_group=null`.** Handled by **Assembly** (§5.1) — it writes the assembled bytes to `target_path` unchanged and flips the file to `Finalized` directly. No reverse worker, no engine. (Used for the pass-through formats, and for the rename-to-TXT group whose final extension is already encoded in `original_relative_path`.)
- **`reverse_conversion` = the original source format → undo the forward conversion** (the common case, e.g. forward `heic→PNG`, reverse `PNG→heic`; `reverse_converter_group=image`).
- **`reverse_conversion` = some other format → convert to a different target type** than the original (e.g. forward `pdf→DOCX`, reverse `DOCX→PDF`; `reverse_converter_group=doc`).

A **ReverseConverter.Worker** for the matching engine group (`files.reverse.image` / `files.reverse.doc`) consumes the two non-pass-through cases:
- Reads `work/{job_id}/assembled/{file_id}.bin`.
- Runs `IFileConverter` from the assembled file's current format (`applied_conversion` if a forward conversion was applied, else `original_format`) **to** `expected_file.reverse_conversion` — the per-`callingSystemId` reverse target A resolved from its `conversion_rule` and carried verbatim in the mini-manifest (§3.6/§6.1). This **replaces** the old "reverse is the inverse of `applied_conversion`" rule: reverse conversion is an independent, configurable target.
- Writes result to `target_path/<original_relative_path>` via `.tmp + rename` (idempotent; deterministic path). `target_path` is a network share (NFS/SMB) for now, written **directly** from this pod (no relay); `.tmp + rename` is safe there and the consuming system scanning the directory ignores `.tmp` files. If two jobs target the same `target_path` and filenames collide, the write overwrites (last-writer-wins) — `.tmp + rename` overwrites naturally.
- `original_relative_path` is taken verbatim from the mini-manifest; Network A's Prepare already encodes nested-archive containers into it: a supported archive container `name.ext` (zip/rar/7z or any other supported archive type) contributes a directory named by replacing the **last** dot of its filename with an underscore (`name_ext`), so `parent.zip/inner.zip/leaf.docx` lands at `target_path/parent_zip/inner_zip/leaf.docx` and a multi-dot container `my.data.zip` becomes `my.data_zip`. The leaf file keeps its true extension. NetworkB does not recompute this layout — it uses `original_relative_path` as-is.
- Done-CAS `expected_file.status Assembled→Finalized` (single transition; no intermediate `ReverseConverting`). The `.tmp + rename` write happens before this CAS, so a redelivery re-renders identical bytes and the CAS still flips exactly once.
- Inserts `outbox(queue='files.finalized', payload)` in the CAS-winning tx.

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
-- SLA minutes resolved per callingSystemId from calling_system_config, default fallback (§6.1).
WITH t AS (
  SELECT j.id FROM job j
   LEFT JOIN calling_system_config csc ON csc.calling_system_id = j.calling_system_id
   LEFT JOIN calling_system_config def ON def.calling_system_id = 'default'
   WHERE j.status IN ('Awaiting','Assembling')
     AND now() - coalesce(j.job_header_received_at, j.created_at)
           > make_interval(mins => COALESCE(csc.sla_minutes, def.sla_minutes, 30))
   LIMIT 10 FOR UPDATE SKIP LOCKED
)
UPDATE job SET status='ReportPending', updated_at=now()
  WHERE id IN (SELECT id FROM t) AND status IN ('Awaiting','Assembling')
  RETURNING id;
```

For each timed-out job: flip non-terminal `expected_file` rows to `Failed`, build payload with `JobStatus='Timeout'`, write CSV + dispatch callback, final CAS to `TimedOut`.

**SLA = per-`callingSystemId`, default 30 minutes** (resolved from B's `calling_system_config` with `default` fallback, §6.1), measured from `coalesce(job_header_received_at, created_at)`. The A-side SLA (§3.7) lives in a separate per-network table; keep both aligned per `callingSystemId`. `ReportPendingSweeper`'s stuck-`ReportPending` threshold is likewise per-`callingSystemId` (`report_pending_minutes`, default 5).

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

Two-stat synchronous: `stat` → sleep 200ms → `stat`, compare size + mtime. This is now belt-and-suspenders: the proxy publishes its Rabbit message only after the file has finished moving into the proxy delivery dir (§4), and NetworkB acts on that message rather than a filesystem-watch event, so it never observes a mid-write file. The check is retained as a cheap second line of defense.

---

## 6. Cross-cutting Concerns

### 6.1 Configuration

**Everything that varies by caller is keyed by `callingSystemId` with a `default` fallback row.** This covers proxy/processing configuration (folder locations, split file-size limit, required forward conversion, required reverse conversion), SLAs and timeouts. The model is split across two table families:

**(a) `conversion_rule` — Network A only, the per-caller processing rule.** Keyed `(calling_system_id, source_format)`. One row carries the proxy configuration the client described:

```jsonc
{
  "SourceFormat":       "heic",   // the file's original_format this rule matches
  "RequiredConversion": "PNG",    // forward conversion on A → source_file.applied_conversion; null = pass-through
  "reverseConversion":  "heic",   // reverse target on B; null = no reverse conversion (pass-through); may differ from SourceFormat
  "FileSizeLimitMb":    200,      // Split.Worker max bytes per chunk → source_file.file_size_limit_mb
  "ConverterGroup":     "image"   // engine routing: "image"|"doc"|"video" → files.convert.{group}; null = no engine (pass-through/rename) → straight to files.split. NB reverse group is derived from reverseConversion and carried in the mini-manifest (§3.6).
}
```

**Engine grouping (§3.1/§5.1, decision §10 #23).** `ConverterGroup` is derived from the client's File Conversion Table: image formats → `image` (ImageMagick→PNG), `pdf`/`ppt`/`pptx` → `doc` (Aspose→DOCX), audio+video → `video` (ffmpeg→MP4/H264/AAC). The 10 pass-through formats and the 5 rename-to-TXT formats (`obj`/`mtl`/`tfw`/`csv`/`upscsv`) carry `ConverterGroup=null` — no engine runs; the rename group's `.txt` relabel is a transit-naming detail carried in `applied_conversion`, not a converter invocation, and the final extension is already in `original_relative_path`. Network B's reverse group (`image`/`doc`/null — never `video`, since all A/V deliver as-is) is computed on A from `reverseConversion` and carried in the mini-manifest's `reverseConverterGroup`.

Resolution precedence (most specific wins): `(callingSystemId, sourceFormat)` → `(callingSystemId, 'default')` → `('default', sourceFormat)` → `('default', 'default')`. **Prepare.Worker** resolves the rule once (moved here from Convert — Prepare must resolve `converter_group` to route, and pass-through files skip Convert) and **snapshots** `applied_conversion`, `reverse_conversion`, `file_size_limit_mb`, and `converter_group` onto the `source_file`, so a mid-job config change cannot make a job's forward/reverse/split parameters inconsistent. `reverse_conversion` then rides A→B in the per-file mini-manifest (§3.6) — **Network B holds no conversion config**, it obeys the carried instruction (§5.5).

**(b) `calling_system_config` — both networks, per-caller folders + SLA.** Keyed by `calling_system_id` (literal `'default'` row = fallback). Network A: outbox folder locations + A-side `sla_minutes` (§3.7). Network B: B-side `sla_minutes` (§5.7) + `report_pending_minutes` (§5.6). **SLA lives in independent per-network tables** (no A→B carry); the operator sets both and keeps them aligned per `callingSystemId` so a job times out coherently across the two sides. *(Per-caller folder locations: if a `callingSystemId` overrides the A outbox dirs, the proxy must watch each such dir — coordinate with the proxy operator; otherwise the `default` row's dirs are used.)*

- `max_retries` per phase lives in `phase_config` (default 5). **Broker-level** timers — RabbitMQ `consumer_timeout`, AMQP connection-heartbeat interval, per-channel `prefetch` — are queue/node-scoped deployment settings (RabbitMQ config / policies / `basic.qos`), **not per-`callingSystemId`** and not DB config — see §3.3 calibration. Only the business-level job SLA is per-caller.
- The flat key/value `config` table remains for genuinely global, non-per-caller knobs (e.g. `outbox` poll interval, `DRAIN_BATCH`, retention days).
**Job priority by `callingSystemId` (enabled — RabbitMQ 4.x).** `calling_system_config.priority` (both networks, `default` fallback) sets a job's RabbitMQ message priority. The flow uses **quorum-queue native message priority** (4.x) — no priority-lane topology, no extra queues:

- **Resolve + snapshot once.** Network A resolves the priority at ingestion and snapshots it onto `job.priority`; Network B resolves it from **its own** `calling_system_config` when it creates the job row from the job header, and snapshots onto `job.priority`. Priority is **independent per network** (same model as SLA, §3.7/§5.7) — it is *not* carried in the job header; the operator keeps the two sides aligned per `callingSystemId`.
- **Carry on every outbox row.** Each worker copies `job.priority` into the `outbox.priority` of the next-step message(s) it inserts. **OutboxRelay** sets the AMQP `priority` property from `outbox.priority` on publish (§3.3 relay loop).
- **32 strict levels (0–31).** RabbitMQ 4.x quorum queues honor **32 priority levels** via the AMQP `priority` property (higher = dispatched first), always enabled — `x-max-priority` is not used (it applies only to classic queues and is ignored by quorum). A message with no `priority` property defaults to **4** (note: classic queues default to 0). So per-`callingSystemId` priority can be a genuinely **graded** 0–31 value, not just two tiers. Set `calling_system_config.priority` accordingly (suggested baseline `4` for "normal", higher for more-urgent callers).
- **Priority is strict — mind starvation.** Quorum priority is *strict*: a higher-priority message is always dispatched ahead of a lower one. There is no built-in fairness, so a `callingSystemId` that submits high-priority work continuously **can starve** lower-priority jobs. Keep the spread small and reserve the top of the range for genuinely urgent callers; watch per-priority queue depth in metrics.
- **Survives retry.** The `priority` property is preserved across the shared `retry`/`reroute` round-trip (TTL expiry → reroute), so a retried high-priority message stays high.
- **Scope.** Because every queue is quorum and every publish goes through OutboxRelay, priority applies **end-to-end** across all phases (Prepare→Convert→Split on A; Assembly→ReverseConvert→Report on B) with no per-queue change.

### 6.2 Idempotency keys

- Primary: **per-row status done-CAS** (`WHERE status='Prev'`) on every state flip — the single serialization point per step.
- Secondary: UNIQUE constraints on natural business keys (`source_file`, `chunk`, `expected_file`, `expected_chunk`).
- **No job-level idempotency on `ExternalId`.** `external_id` is a pass-through correlation id only — **not unique**, not a dedup key. Every ingestion submission creates a new `job` (QUESTIONS §6). Consequence: at-least-once ingestion (RabbitBridge redelivery, HTTP client retry, watcher re-pickup) can create duplicate jobs; if that becomes a problem, the affected channel adds its own dedup (e.g. message-id), not the `job` table.
- Outbox: `outbox.id` is the publish identity; relays are at-least-once (consumers remain idempotent via CAS).

### 6.3 Observability

- **Structured logs (Serilog)** — `job_id`, `service`, `step`, `worker_id` (the processing pod, from logs/env — not a DB column), `message_id`.
- **Metrics (Prometheus)** — RED per worker step (**per-step duration histogram drives `consumer_timeout` calibration, §3.3**); queue depths; **`redelivered`-message rate per queue** (the replacement for the old sweeper-rescue metric — a rising rate signals crashing/timing-out workers); `outbox` backlog (pending rows + max age) per network; Postgres pool / autovacuum / replication lag.
- **Tracing (OpenTelemetry)** — `job_id` as baggage.
- **DLQ alerts** — any `*.dead` message > 5 min pages.
- **Outbox alerts** — outbox backlog > N for > N min pages.
- **Redelivery alerts** — `redelivered` rate above baseline indicates churning/crashing/timing-out workers (was: heartbeat-staleness / sweeper-rescue alert).

### 6.4 Scaling for 1M jobs/month

- **Workers** scale horizontally per phase.
- **Converter workers scale + size per engine group** (§3.1/§5.1). Each of `files.convert.{image,doc,video}` (A) and `files.reverse.{image,doc}` (B) is an independent deployment with its own HPA, node resources, and image: `image` = small/fast/many replicas; `doc` = memory-heavy, Aspose license-capped replicas; `video` = large CPU+RAM pods, A-only (B has no video reverse). **The 6 GB video chunk limit is a separate sizing concern** — it stresses Split, proxy transit, and B's concat/assembly memory + disk, and likely needs a longer SLA than other groups ([`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §22).
- **OutboxRelay** — 2–3 replicas per network competing via `FOR UPDATE SKIP LOCKED`.
- **RabbitMQ 4.3** — quorum queues, **3-node cluster per network** (odd-sized for Raft majority; each queue replicated across all 3 → survives one node loss). Topology as `definitions.json`; tunables (`delivery-limit=20` (4.x default; pinned explicitly), `consumer_timeout`, `dead-letter-strategy=at-least-once`, `x-max-length`, TTL) as policies. Native quorum message priority drives per-`callingSystemId` job priority (§6.1) — no priority-lane queues. Single-node = broker SPOF, [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §20.
- **PostgreSQL 16** — one cluster per network; no partitioning day-1. Year-2: monthly partitioning on `chunk`, `expected_chunk`, `outbox`. PgBouncer in transaction-pooling mode.
- **Job-level sweepers** (Network B only: `TimeoutSweeper`, `ReportPendingSweeper`) — single sweeper-enabled replica (`SWEEPER_ENABLED=true`). Per-row CAS gates make duplicate sweepers safe. There are no per-phase row-heartbeat sweepers (removed; see §3.3).

### 6.5 Storage tier choice

The client confirmed (interim) that every pod can read **and** write-many to every folder ([`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §15/§16), so all shared dirs are treated as RWX-capable with **no node-affinity pinning**.

| Volume | Default | Why |
|--------|---------|-----|
| **A `work/{job_id}/`** | RWX PV | Throughput modest at 4 jobs/sec; RWX assumed (every pod can read+write). |
| **A outbox dirs (data + manifest)** | RWX filesystem | Proxy contract — filesystem-watched; written via `.tmp + rename` (proxy ignores `.tmp`). |
| **B proxy delivery dir** | RWX PV (shared by ProxyListener + Assembly) | Chunks live here through assembly; Assembly reads in-place during concat and best-effort deletes consumed chunks post-COMMIT; Reporter sweeps the remainder on terminal job and the proxy self-cleans days-old leftovers. |
| **B `work/{job_id}/assembled/`** | RWX PV | Assembled file output (concat target). |
| **B target_path** | Network share (NFS/SMB) | Client interface; written directly from the ReverseConverter pod via `.tmp + rename`; overwrite on filename collision (last-writer-wins). |

`IStorage` abstracts only the filesystem binding. No S3-compatible object store is available ([`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §10) — all volumes are filesystem-backed.

### 6.6 Retention & cleanup

- **A:** when proxy completes pickup, Split.Worker's cleanup cron deletes chunk files and `work/{job_id}/`. Backup cron sweeps outbox dirs older than `proxy_max_transit + safety_margin`.
- **B (per assembled file):** Assembly best-effort deletes the consumed chunk files from the proxy delivery dir immediately after the assembling COMMIT (post-COMMIT, failures ignored), freeing disk incrementally instead of at job end.
- **B (terminal job):** Reporter (on `job.status IN ('Done','Failed','TimedOut','PartiallyDone')`) deletes:
  1. `work/{job_id}/assembled/` (assembled intermediates)
  2. Any chunk files still remaining in the proxy delivery dir for this job (most were already deleted by Assembly post-COMMIT; this catches chunks of Failed/partial files Assembly never consumed), referenced by `expected_chunk.name`
  3. Job-header file and all per-file manifest files and sentinels in the proxy delivery dir for this job
  After deletes succeed, sets `job.cleaned_at=now()`. As a final backstop, the proxy self-cleans days-old leftover files from its delivery dir.
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

Each invariant is detailed in its canonical section; this is the audit index.

- [x] **Idempotent step + single done-CAS, ack-after-COMMIT** (§3.3) — no `-ing` states; an unacked message always reflects unfinished work; a redelivery/concurrent-duplicate loses the CAS and no-ops (state, counters, and side-effects all gated by it).
- [x] **Recovery = broker redelivery + client-side watchdog** (§3.3) — crash/network-death via channel close (seconds–~60s), deadlock via watchdog/`consumer_timeout`; `prefetch=1` caps requeue blast radius; quorum `delivery-limit` / app `max_retries=5` is the poison backstop. No per-phase row heartbeat or sweeper.
- [x] **Next-step publish via transactional outbox → OutboxRelay** (§3.3) — ~500ms; closes the post-COMMIT-pre-publish gap.
- [x] **Counters incremented in the CAS-winning tx, gated by the status guard** (§3.4) — exactly-once across redeliveries/duplicates; no `manifest_written` flag.
- [x] **Write-before-commit for any file NB depends on** (§3.4 step 5, §3.5 DLQ, §3.7 sweeper) — mini-manifest / assembled bytes land on disk before the terminal-status commit; a redelivery re-writes identical bytes. The mini-manifest carries per-file failure status so NB never waits on chunks that will never arrive.
- [x] **Deterministic on-disk names + `.tmp` + rename everywhere** (§4.2) — safe concurrent-duplicate rewrites; the proxy and `target_path` consumer ignore `.tmp`; Assembly concat sequenced before the outbox insert (§5.3) → no publish-vs-file race.
- [x] **Early arrivals buffered, not NACK-looped** (§5.3) — `inbox.published=FALSE`; file-manifest handler inline-drains its chunks in one tx; job-header handler drains in bounded batches (`DRAIN_BATCH`, job row committed first → resumes idempotently on redelivery).
- [x] **Job-level liveness backstops** — Network B `TimeoutSweeper` (§5.7); Network A `files_manifest_written_count` + per-file DLQ-terminal + `TimeoutSweeper-A` (§3.7). Both 30-min SLA; guarantee terminal even for a watchdog-evading hang or a wedged dead-letter target.

---

## 7. What's removed vs. current

| Removed | Why |
|---------|-----|
| Temporal (workflows, signals, activities) | Replaced by Rabbit + Postgres + idempotent workers. |
| SHA256 compute & verify | Per user instruction; deterministic chunk names + UNIQUE constraints provide placement integrity. |
| Global "wait for all chunks of a package" | Per user instruction; per-file batch finalization. |
| Repack of nested archives on NetworkB | Per user instruction; assembled files placed at their `original_relative_path` under `target_path`, with archive containers rendered as `name_ext` directories (last dot → underscore; encoded by Prepare on Network A, see §5.5). |
| `processed_message`, `counter_event`, `file_finalized_dedup`, `split_completed` | Replaced by per-row status CAS. |
| **Per-row heartbeat loop + per-phase heartbeat sweeper** | Replaced by broker redelivery + `consumer_timeout` (§3.3). Crash/network-death recover via channel/connection close; deadlock via `consumer_timeout`. Crash recovery is *faster* than the old ~90s sweeper because no `-ing` row blocks the redelivery from reprocessing. |
| **Intermediate `-ing` states** (`Preparing`, `Converting`, `Splitting`, `ReverseConverting`) | Replaced by done-states only. Each step does idempotent work then a single done-CAS; no claim/in-progress state to get stuck or to require sweeping. |
| **`worker_id` / `last_heartbeat_at` columns + `*_stale_hb` indexes** | Removed from `job`/`source_file`/`expected_file` (A and B). Processing-worker visibility comes from logs/traces, not DB columns. |
| **`source_file.manifest_written` flag** | Redundant under done-states: the `Converted→Split` (and terminal-Failed) CAS is itself the exactly-once counter gate. |
| Advisory locks, consistent-hash exchange, leader-elected sweepers, day-1 partitioning, session-pool PgBouncer route, path-sharded work dirs, async two-stat stability check | Deferred. |
| NACK+requeue for chunks-before-manifest | Replaced by inbox buffering (`published=FALSE`) + manifest-time inline drain. |
| Per-chunk holding files in NetworkB work dir | Replaced by reading chunks in-place from the proxy delivery dir during concat. |
| Single end-of-job manifest | Replaced by job header (`{job_id}.job.json` written by Prepare) + per-file mini-manifests (`{job_id}_{source_file_id}.file.json` written by Split). NB starts assembling each file as soon as its mini-manifest and chunks arrive. |
| `ManifestPending` / `ManifestWritten` job states | Replaced by `files_manifest_written_count` counter + `AllManifestsWritten`. No single "last writer" coordination; each Split.Worker handles its own file independently. |
| ManifestSweeper | Eliminated. Per-file write-before-commit sequencing + broker redelivery covers all crash scenarios without a dedicated sweeper. |

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
    Dintinct.A.Convert.Worker/                # one codebase; deployed 3× by CONVERTER_GROUP (image/doc/video), one queue + image each (§3.1)
    Dintinct.A.Split.Worker/                  # writes per-file mini-manifests to proxy outbox; Prepare writes job header + routes engine-vs-passthrough
    Dintinct.A.OutboxRelay/                   # relays outbox rows → RabbitMQ
    Dintinct.A.DlqConsumer/                   # binds *.dead; write-before-commit mini-manifest + source_file Failed
    Dintinct.A.Domain/
    Dintinct.A.FileProcessing/
    Dintinct.A.Infrastructure/
  NetworkB/
    Dintinct.B.ProxyListener/
    Dintinct.B.Assembly.Worker/
    Dintinct.B.ReverseConverter.Worker/       # one codebase; deployed 2× by CONVERTER_GROUP (image/doc), one queue + image each (§5.1); no video
    Dintinct.B.Reporter.Worker/
    Dintinct.B.OutboxRelay/
    Dintinct.B.DlqConsumer/                   # binds *.dead; expected_file Failed (no manifest write on B)
    Dintinct.B.Domain/
    Dintinct.B.FileAssembly/
    Dintinct.B.Infrastructure/
  Shared/
    Dintinct.Shared.Contracts/
    Dintinct.Shared.Messaging/                # consumer base: prefetch=1, done-CAS helper, ack-after-commit
    Dintinct.Shared.Storage/
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

1. Average files-per-job (§8) — client has no data; average job ~5 min excluding proxy transit. Drives year-2 partitioning + ingestion-split trade-off.
2. Storage retention SLA per volume (§9).

Resolved in this revision (recorded for traceability):

| # | Item | Resolution |
|---|------|------------|
| 1 | Outbox scope | Both A and B. OutboxRelay per network. |
| 2 | ~~Heartbeat intervals~~ → **Recovery model (revised)** | Per-row heartbeat + per-phase sweeper **removed**. Recovery = broker redelivery + `consumer_timeout` (§3.3). `consumer_timeout` sized from measured max step time (calibration); `prefetch=1`; AMQP connection-heartbeat 60s. Per-phase heartbeat retained only as an escape hatch via `phase_config`. |
| 3 | `max_retries` default | 5. |
| 4 | A-side Failed analog | `source_file.status='Failed'`. Per-file mini-manifest carries failure status so NB creates `expected_file` terminal directly. |
| 5 | Transient/permanent | Exception-type whitelist (§3.5, §5.8). Unknown → transient. |
| 6 | Buffered chunk byte location | Read in-place from proxy delivery dir; no copy. RWX PV shared by ProxyListener + Assembly. |
| 7 | Concat owner | Assembly, inside the winning-CAS handler, before COMMIT (file IO sequenced before outbox insert). |
| 8 | Timeout SLA | 30 minutes from `coalesce(job_header_received_at, created_at)`. |
| 9 | End-of-job manifest | Replaced by job header (Prepare) + per-file mini-manifests (Split). NB can start assembling each file immediately on mini-manifest arrival; no global wait. |
| 10 | Manifest write crash safety | Write-before-commit sequencing: mini-manifest written to disk BEFORE committing the done-CAS terminal status. Broker redelivery → retry → re-write (idempotent) → commit. The `source_file.status` done-CAS is itself the exactly-once counter gate (no `manifest_written` flag). |
| 11 | NB job row creation trigger | Job header arrival (via Proxy) — `job_header_received_at` replaces `manifest_received_at`. `expected_file_count` set from job header's `totalSourceFiles`. |
| 12 | ProxyListener branch count | 7 branches (was 3): chunk-before-file-manifest, job-header, file-manifest (job exists), file-manifest (no job), chunk-after-file-manifest, file-manifest ERROR sentinel, job-header ERROR sentinel. |
| 13 | Proxy-outbox safe-write strategy | `.tmp` + rename everywhere (the proxy ignores `.tmp` files). Staging-subdir + mv workaround reverted; partial-file-pickup risk closed (§4.1/§4.2). |
| 14 | Proxy→B truncation guard | Proxy publishes its Rabbit message only after the file finishes moving into the proxy delivery dir; NB acts on the message, never a filesystem-watch event, so it never sees a mid-write file. Two-stat stability check kept as belt-and-suspenders (§4/§5.9). |
| 15 | Chunk lifecycle / disk reclaim | Assembly reads chunks in-place, then best-effort deletes consumed chunks post-COMMIT per file; Reporter sweeps the remainder on terminal job; proxy self-cleans days-old leftovers as backstop (§5.1/§5.3/§6.6). |
| 16 | Storage tier / object store | RWX assumed for all shared dirs, no node-affinity pinning (§15/§16); no S3-compatible store available (§10) — all volumes filesystem-backed; `IStorage` abstracts only the filesystem binding (§6.5). |
| 17 | Nested-archive output layout | Archive container `name.ext` → directory `name_ext` (last dot → underscore; multi-dot names replace only the last), baked into `original_relative_path` by Prepare; NB uses it verbatim. Leaf keeps its true extension (§5.5). |
| 18 | target_path destination | Network share (NFS/SMB), written directly from the ReverseConverter pod via `.tmp` + rename; overwrite (last-writer-wins) on filename collision (§5.5/§6.5). |
| 19 | 4-channel ingestion split | Confirmed — 4 separate ingestion services, one per channel (§3.1). |
| 20 | Per-`callingSystemId` configuration | All caller-varying config (folders, split file-size limit, forward + reverse conversion, SLA, timeouts) keyed by `callingSystemId` with a `default` fallback. A-side processing rule in `conversion_rule` (`(callingSystemId, source_format)`, resolution precedence in §6.1); per-network folders + SLA in `calling_system_config`. SLA is independent per network (no A→B carry); operator keeps both sides aligned. |
| 21 | Reverse conversion is configurable, carried via manifest | Reverse conversion is no longer the inverse of the forward conversion: it is an independent per-`(callingSystemId, source_format)` target (`reverse_conversion`) that may be `null` (skip / pass-through), equal the original format, or name a different type. A resolves it; the per-file mini-manifest carries it to B (§3.6); B keeps no conversion config and obeys it (§5.5). Schema: `source_file.reverse_conversion`/`file_size_limit_mb` (A), `expected_file.reverse_conversion` (B). |
| 22 | Job priority by `callingSystemId` | **Enabled** — stack upgraded to **RabbitMQ 4.3**, whose quorum queues have native message priority (**32 strict levels, 0–31**; default 4). `calling_system_config.priority` (per network, default fallback) → snapshot `job.priority` → `outbox.priority` → OutboxRelay sets the AMQP priority on publish. End-to-end, no priority-lane queues. Strict priority → mind starvation. §6.1, [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) §20/§21. |
| 23 | Converter engine grouping + pass-through skip | The single `Convert.Worker` becomes **3 engine-grouped deployments of one codebase**, each with its own queue + `consumer_timeout`: `files.convert.image` (ImageMagick, image→PNG), `files.convert.doc` (Aspose, pdf/ppt/pptx→DOCX), `files.convert.video` (ffmpeg, A/V→MP4/H264/AAC). Network B mirrors with **2** reverse deployments — `files.reverse.image`, `files.reverse.doc` (no video: all A/V Target Format = "No conversion needed"). Files needing no engine (10 pass-through + 5 rename-to-TXT formats) carry `converter_group=NULL` and **skip the converter**: Prepare routes them straight to `files.split` (A); Assembly writes the assembled bytes straight to `target_path` (B). Routing: A resolves `conversion_rule.converter_group`; B obeys `reverseConverterGroup` carried in the mini-manifest. Shared retry/dead unchanged (+3 main queues A, +2 B). Rule resolution+snapshot moved Convert→Prepare. Grouping derived from the client's File Conversion Table. See BLOCKERS S7, §3.1/§5.1/§6.1. |
