# Dintinct v3 — Executive Summary

## What changes

The Dintinct pipeline is being rebuilt **without Temporal**. The business logic (decomposition, splitting, conversion, assembly, reverse conversion, reporting) is preserved verbatim. The orchestration layer is replaced by a simple, explicit, **idempotent step-services + RabbitMQ + PostgreSQL** pattern, one stack per network, with **per-row done-state CAS + transactional outbox** as the crash-recovery primitive. Worker liveness/recovery is handled by RabbitMQ message redelivery + a client-side watchdog (and `consumer_timeout` on quorum queues), backstopped by job-level sweepers on both networks — not by a per-row heartbeat or per-phase sweeper.

## Why

- **No framework lock-in.** Plain .NET workers, plain Rabbit, plain Postgres.
- **Operational simplicity.** Crash recovery is built into the worker contract (done-CAS + outbox) and the broker's own redelivery, not delegated to a workflow engine or a bespoke sweeper layer.
- **Per-network isolation enforced by infra.** Postgres-A and Postgres-B are separate; A and B do not share anything except the Proxy bridge.
- **Throughput.** 1M jobs/month (~230/min peak) is comfortably handled by horizontally scaled workers per phase.

## The pipeline in one breath

NetworkA: **Ingest** (4 channels) → enqueue via outbox → **Prepare** (unpack; also writes job header `{job_id}.job.json` to proxy outbox after done-CAS `Created→Prepared`) → **Convert** → **Split** (each file writes its own mini-manifest `{job_id}_{source_file_id}.file.json` to proxy outbox on completion or permanent failure) → drop files into proxy outbox dirs (all writes via `.tmp` + rename; the proxy ignores `.tmp` files).

Proxy: ships files unchanged, publishes a Rabbit message per file to NetworkB.

NetworkB: **ProxyListener** classifies by filename pattern into 7 branches (JobHeader, FileManifest, Chunk, JobHeaderError, FileManifestError, Error, Unsupported — publishing to `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, `chunks.received`, etc.; chunks-before-file-manifest and file-manifests-before-job-header are buffered in `inbox` with `published=FALSE` and never copied) → **Assembly** drains buffered file-manifests when the job header arrives and buffered chunks when each file-manifest arrives, runs concat (reading chunks in-place from the proxy delivery dir) inside the same handler before COMMIT, then best-effort deletes the consumed chunks after COMMIT, emits `files.assembled` → **ReverseConverter** applies the manifest-carried per-`callingSystemId` `reverse_conversion` (pass-through if `null`, else convert to that target type) and writes to `target_path` → **Reporter** writes the CSV and dispatches `StatusCallbackPayload` once the job is fully terminal.

A separate **OutboxRelay** process per network publishes durable outbox rows to RabbitMQ with publisher-confirm and marks `published_at`.

## Headline design choices

1. **Per-row done-state CAS is the universal dedup primitive; broker redelivery + a client-side watchdog are the recovery primitives.** Each step does its idempotent work, then `UPDATE row SET status='Done' WHERE id=$1 AND status='Prev' RETURNING id` — the single serialization point (it also gates the outbox insert and any counter increment). No intermediate `-ing` states, no per-row heartbeat, no per-phase sweeper. A crashed/hung worker's unacked message is requeued by RabbitMQ — seconds for a crash, ~60s for a network partition. An app deadlock (connection alive) is reclaimed by a **client-side watchdog** (`CancellationTokenSource(maxStepDuration)`) in seconds — the primary defense, since it beats the coarser `consumer_timeout` (enforced on quorum queues, evaluated at 1-min intervals, 30-min default). See ARCHITECTURE.md §3.3.
2. **Transactional outbox for downstream publishes — both A and B.** Each worker inserts the next-step message into `outbox(queue, payload, published_at=NULL)` in the same transaction as its state flip; a per-network `OutboxRelay` (2–3 replicas) polls every ~500ms, publishes with publisher-confirm, and marks `published_at=now()`. Closes the post-COMMIT-pre-publish gap.
3. **Postgres per network**, flat tables day-1. Year-2 monthly partitioning on `chunk`, `expected_chunk`, `outbox` if row counts justify.
4. **RabbitMQ 4.3 per network** — **quorum queues** (decided), **3-node cluster** (odd-sized for Raft majority → each queue replicated, survives one node loss; single node = broker SPOF, QUESTIONS §20). One retry tier (1-min TTL, `max_retries=5`) + DLQ. Quorum gives the backstops this design uses: `consumer_timeout` (enforced on quorum, default 30 min), `delivery-limit=20` (4.x default; pinned via policy), `dead-letter-strategy=at-least-once` (with `reject-publish` overflow + DLX, no silent loss), and **native message priority** (32 strict levels, 0–31) driving per-`callingSystemId` job priority (§6.1). Internal queues use `x-overflow=reject-publish` so OutboxRelay sees nacks; outbox backlog metric signals upstream backpressure; HTTP 429 surfaces at Ingestion when the chain is full.
5. **Per-file batch assembly on NetworkB** — triggered by per-file mini-manifests (`{job_id}_{source_file_id}.file.json`), each carrying `byte_length` per chunk. Buffering works at 2 levels: chunks-before-file-manifest (buffered in `inbox`, drained when file-manifest arrives) and file-manifests-before-job-header (buffered in `inbox`, drained when job header arrives; 3-level drain on job-header handler, risk accepted). **Chunk bytes are never copied** — Assembly reads them in-place from the proxy delivery dir during concat, then best-effort deletes the consumed chunks after COMMIT to free disk incrementally (Reporter sweeps any remainder on terminal; the proxy self-cleans days-old leftovers). Each file finalizes independently when its chunks are all present.
6. **Assembly concats inside the winning-CAS handler before COMMIT.** File IO is sequenced before the outbox insert so OutboxRelay can never publish `files.assembled` before the assembled file exists on disk. ReverseConverter does pure reverse-conversion — it never touches chunks or the proxy delivery dir.
7. **A-side failures carried in per-file mini-manifests.** When a `source_file` ends in `Failed`/`NotSupported`, its mini-manifest (`{job_id}_{source_file_id}.file.json`) carries that status and `failure_reason`. NB's Assembly (`file-manifest-errors.received` handler) creates `expected_file` directly in the terminal state with no `expected_chunk` rows and emits `files.finalized`; Reporter's terminal-count picks them up immediately.
8. **Worker exception handling: transient vs permanent (exception-type whitelist).** Transient (`SqlException`, `BrokerUnreachableException`, `IOException`, `TimeoutException`, etc.) → `nack(requeue=false)` → `*.retry` (1-min TTL) → up to 5 retries. Permanent (`UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException`) → `UPDATE source_file/expected_file SET status='Failed'` → `nack(requeue=false)` → `*.dead`. Unknown → transient by default. **Job-level `Failed` is set ONLY by Reporter (all files failed) or TimeoutSweeper (SLA exceeded).** Workers never set `job.status='Failed'` directly.
9. **Timeout SLA = 30 minutes** from `coalesce(job_header_received_at, created_at)`. Configurable per deployment.
10. **Manifest carries `byte_length` per chunk** (replacing SHA256).
11. **No advisory locks, no consistent-hash routing.** Per-row CAS + WHERE clauses + outbox give all race-safety needed.
12. **RWX assumed for all shared dirs** (client-confirmed interim: every pod has read/write-many access to all folders). Proxy delivery dir on B is RWX shared between ProxyListener and Assembly (chunks read in-place + best-effort deleted post-COMMIT). **No S3 available — filesystem-only.**
13. **PgBouncer** (transaction pooling) in front of each Postgres; **schema migrations via OpenShift Job**; **Sealed Secrets** for credentials.
14. **Job-level sweepers** run on a single replica (env-var enabled) — no leader election, no per-phase row sweepers. Network B: `TimeoutSweeper`, `ReportPendingSweeper`. Network A: `TimeoutSweeper-A` (ARCHITECTURE.md §3.7, co-located on an OutboxRelay-A replica) forces any `source_file` stranded by a watchdog-evading hang or a wedged dead-letter target to `Failed`, so `AllManifestsWritten` is always reached. Per-row CAS gates protect correctness under accidental concurrent runs.
15. **Frozen contracts** with the outside world: `IngestionRequestPayload`, `StatusCallbackPayload`, final CSV (`status,dir_path`), proxy Rabbit message shape.
16. **Per-`callingSystemId` configuration with a `default` fallback.** Everything that varies by caller — proxy folders, Split file-size limit, forward conversion, reverse conversion, SLA/timeouts — is keyed by `callingSystemId`. Network A holds the processing rule (`conversion_rule` keyed `(callingSystemId, source_format)`, precedence `(sys,fmt)→(sys,default)→(default,fmt)→(default,default)`); per-caller folders + SLA live in `calling_system_config` on **both** networks (SLA independent per network, operator keeps them aligned). Convert.Worker snapshots the resolved rule onto `source_file` so a mid-job config change can't split a job.
17. **Reverse conversion is an independent, manifest-carried instruction.** It is no longer the inverse of the forward conversion: A resolves `reverse_conversion` from the rule (may be `null`=skip, equal the source format, or a *different* target type) and carries it to B in the per-file mini-manifest. Network B keeps no conversion config — ReverseConverter obeys the carried value (pass-through on `null`).
18. **Job priority by `callingSystemId` (RabbitMQ 4.3).** `calling_system_config.priority` (per network, default fallback) is snapshotted onto `job.priority`, copied to every `outbox.priority`, and set by OutboxRelay as the AMQP message priority. 4.x quorum queues honor native message priority (**32 strict levels, 0–31**, default 4) — so priority applies end-to-end with **no priority-lane queues**, and callers can be ranked on a graded scale (not just two tiers). Priority is *strict* (no fairness) — sustained high-priority load can starve lower levels, so keep the spread tight. Independent per network, like SLA; operator keeps the two sides aligned.

## Service count

- **NetworkA:** 9 services (4 ingestion + Prepare + Convert + Split + OutboxRelay + DLQ.Consumer).
- **NetworkB:** 6 services (ProxyListener + Assembly + ReverseConverter + Reporter + OutboxRelay + DLQ.Consumer).
- **Proxy:** 1 (third-party).

The 4-channel ingestion split was chosen by the user for crash isolation. Two reviewers argued for collapsing it into 1 Ingestion service — surfaced as an open trade-off rather than silently overriding the user's choice.

## What's removed from the current system

- Temporal (workflows, signals, activities)
- SHA256 compute and verify
- Global "wait for all chunks of a package" before processing
- Repack of nested archives on NetworkB (assembled files placed at their original relative path, with each archive container `name.ext` rendered as a `name_ext` directory — e.g. `parent.zip/inner.zip/leaf.docx` → `target_path/parent_zip/inner_zip/leaf.docx`; the leaf keeps its real extension)
- NACK+requeue for chunks-before-manifest (replaced by `inbox` buffering + inline drain)
- **Per-row heartbeat loop + per-phase heartbeat sweeper** (replaced by broker redelivery + `consumer_timeout`; crash recovery is now *faster* — no `-ing` row blocks the redelivery)
- **Intermediate `-ing` states** (`Preparing`/`Converting`/`Splitting`/`ReverseConverting`) and the `worker_id`/`last_heartbeat_at` columns (done-states only)
- **`source_file.manifest_written` flag** (the `Converted→Split` CAS is itself the exactly-once counter gate)
- Per-chunk holding files in NetworkB work dir (chunks read in-place from proxy delivery dir during concat)
- Single big manifest (replaced by job header + per-file mini-manifests)
- `ManifestPending` job state (no more single last-writer coordination)
- ManifestSweeper (per-file write-before-commit + broker redelivery covers recovery)

## Open questions still needing client input

See [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md). Headline items:

1. Average files-per-job (client gave no data; avg job ~5 min excl. proxy delay — drives year-2 partitioning + `consumer_timeout` sizing, deferred post-launch).
2. Storage retention SLA per volume.
3. ~~Converter cardinality~~ — ✅ answered **1→1**; `converted_file` collapsed into `source_file`.
4. ~~4-channel ingestion split~~ — ✅ confirmed as the desired ops surface.
5. ~~RabbitMQ version + queue type~~ — ✅ resolved: **RabbitMQ 4.3, quorum queues, 3-node cluster per network** (§20). Remaining sub-item: confirm 3-node vs single-node (single = accepted broker SPOF).
6. ~~Per-`callingSystemId` configuration model~~ — ✅ resolved (§21): per-caller config with `default` fallback; `conversion_rule` (A) + `calling_system_config` (both); reverse conversion carried in the mini-manifest. **Job priority by `callingSystemId` enabled** on RabbitMQ 4.3 (native quorum message priority) — `calling_system_config.priority` → `job.priority` → `outbox.priority` → AMQP priority on publish; no priority-lane queues.

In progress: converter determinism (Aspose PDF→DOCX) fix is owned by the transform/split implementer (persist-first-output as documented contingency).

All in-document `[NEED CLARIFICATION]` markers from the previous revision are resolved (see ARCHITECTURE.md §10 resolutions table).

## Deliverables in this folder

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — full design, schemas, flow contracts, retry policy.
- [`MICROSERVICES.md`](MICROSERVICES.md) — per-service responsibilities, queues, scale strategy.
- [`SUMMARY.md`](SUMMARY.md) — this file.
- [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) — items needing client decision.
- [`architecture-v3.drawio`](architecture-v3.drawio) — service topology.
- [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) — normal flow, broker-redelivery crash recovery, outbox post-COMMIT recovery, and deadlock recovery via client-side watchdog + `TimeoutSweeper-A` (page 4).
- [`chunk-flow.drawio`](chunk-flow.drawio) — chunk-before-manifest buffering and inline drain.
- [`worker-exception-handling.drawio`](worker-exception-handling.drawio) — transient/permanent failure paths and job-level Failed resolution.
