# Dintinct v3 — Executive Summary

## What changes

The Dintinct pipeline is being rebuilt **without Temporal**. The business logic (decomposition, splitting, conversion, assembly, reverse conversion, reporting) is preserved verbatim. The orchestration layer is replaced by a simple, explicit, **idempotent step-services + RabbitMQ + PostgreSQL** pattern, one stack per network, with a **per-row status CAS + heartbeat + transactional outbox** as the universal crash-recovery primitive.

## Why

- **No framework lock-in.** Plain .NET workers, plain Rabbit, plain Postgres.
- **Operational simplicity.** Crash recovery is built into the worker contract (CAS + heartbeat + outbox), not delegated to a workflow engine.
- **Per-network isolation enforced by infra.** Postgres-A and Postgres-B are separate; A and B do not share anything except the Proxy bridge.
- **Throughput.** 1M jobs/month (~230/min peak) is comfortably handled by horizontally scaled workers per phase.

## The pipeline in one breath

NetworkA: **Ingest** (4 channels) → enqueue via outbox → **Prepare** (unpack; also writes job header `{job_id}.job.json` to proxy outbox after final CAS `Preparing→Prepared`) → **Convert** → **Split** (each file writes its own mini-manifest `{job_id}_{source_file_id}.file.json` to proxy outbox on completion or permanent failure) → drop files into proxy outbox dirs.

Proxy: ships files unchanged, publishes a Rabbit message per file to NetworkB.

NetworkB: **ProxyListener** classifies by filename pattern into 7 branches (JobHeader, FileManifest, Chunk, JobHeaderError, FileManifestError, Error, Unsupported — publishing to `job-headers.received`, `file-manifests.received`, `file-manifest-errors.received`, `chunks.received`, etc.; chunks-before-file-manifest and file-manifests-before-job-header are buffered in `inbox` with `published=FALSE` and never copied) → **Assembly** drains buffered file-manifests when the job header arrives and buffered chunks when each file-manifest arrives, runs concat (reading chunks in-place from the proxy delivery dir) inside the same handler before COMMIT, emits `files.assembled` → **ReverseConverter** does pure reverse-conversion to `target_path` → **Reporter** writes the CSV and dispatches `StatusCallbackPayload` once the job is fully terminal.

A separate **OutboxRelay** process per network publishes durable outbox rows to RabbitMQ with publisher-confirm and marks `published_at`.

## Headline design choices

1. **Per-row status CAS + heartbeat is the universal liveness + dedup primitive.** `UPDATE row SET status='Next', worker_id=$me, last_heartbeat_at=now() WHERE id=$1 AND status='Prev' RETURNING id`. Heartbeat refreshes `last_heartbeat_at` every **10s**; per-phase sweeper resets rows whose heartbeat is older than **30s** (3× the interval) and re-publishes them via the outbox. Defaults configurable per phase in `phase_config`.
2. **Transactional outbox for downstream publishes — both A and B.** Each worker inserts the next-step message into `outbox(queue, payload, published_at=NULL)` in the same transaction as its state flip; a per-network `OutboxRelay` (2–3 replicas) polls every ~500ms, publishes with publisher-confirm, and marks `published_at=now()`. Closes the post-COMMIT-pre-publish gap.
3. **Postgres per network**, flat tables day-1. Year-2 monthly partitioning on `chunk`, `expected_chunk`, `outbox` if row counts justify.
4. **Rabbit per network** with one retry tier (1-min TTL, `max_retries=5`) + DLQ. Internal queues use `x-overflow=reject-publish` so OutboxRelay sees nacks; outbox backlog metric signals upstream backpressure; HTTP 429 surfaces at Ingestion when the chain is full.
5. **Per-file batch assembly on NetworkB** — triggered by per-file mini-manifests (`{job_id}_{source_file_id}.file.json`), each carrying `byte_length` per chunk. Buffering works at 2 levels: chunks-before-file-manifest (buffered in `inbox`, drained when file-manifest arrives) and file-manifests-before-job-header (buffered in `inbox`, drained when job header arrives; 3-level drain on job-header handler, risk accepted). **Chunk bytes are never copied** — Assembly reads them in-place from the proxy delivery dir during concat. Each file finalizes independently when its chunks are all present.
6. **Assembly concats inside the winning-CAS handler before COMMIT.** File IO is sequenced before the outbox insert so OutboxRelay can never publish `files.assembled` before the assembled file exists on disk. ReverseConverter does pure reverse-conversion — it never touches chunks or the proxy delivery dir.
7. **A-side failures carried in per-file mini-manifests.** When a `source_file` ends in `Failed`/`NotSupported`, its mini-manifest (`{job_id}_{source_file_id}.file.json`) carries that status and `failure_reason`. NB's Assembly (`file-manifest-errors.received` handler) creates `expected_file` directly in the terminal state with no `expected_chunk` rows and emits `files.finalized`; Reporter's terminal-count picks them up immediately.
8. **Worker exception handling: transient vs permanent (exception-type whitelist).** Transient (`SqlException`, `BrokerUnreachableException`, `IOException`, `TimeoutException`, etc.) → `nack(requeue=false)` → `*.retry` (1-min TTL) → up to 5 retries. Permanent (`UnsupportedFormatException`, `FileCorruptException`, `ConversionException`, `ValidationException`) → `UPDATE source_file/expected_file SET status='Failed'` → `nack(requeue=false)` → `*.dead`. Unknown → transient by default. **Job-level `Failed` is set ONLY by Reporter (all files failed) or TimeoutSweeper (SLA exceeded).** Workers never set `job.status='Failed'` directly.
9. **Timeout SLA = 30 minutes** from `coalesce(job_header_received_at, created_at)`. Configurable per deployment.
10. **Manifest carries `byte_length` per chunk** (replacing SHA256).
11. **No advisory locks, no consistent-hash routing.** Per-row CAS + WHERE clauses + outbox give all race-safety needed.
12. **Local PV for work dirs.** Proxy delivery dir on B is RWX shared between ProxyListener and Assembly. S3-compatible storage opt-in.
13. **PgBouncer** (transaction pooling) in front of each Postgres; **schema migrations via OpenShift Job**; **Sealed Secrets** for credentials.
14. **Sweepers run on a single replica** (env-var enabled) — no leader election. Per-row CAS gates protect correctness under accidental concurrent runs.
15. **Frozen contracts** with the outside world: `IngestionRequestPayload`, `StatusCallbackPayload`, final CSV (`status,dir_path`), proxy Rabbit message shape.

## Service count

- **NetworkA:** 9 services (4 ingestion + Prepare + Convert + Split + OutboxRelay + DLQ.Consumer).
- **NetworkB:** 6 services (ProxyListener + Assembly + ReverseConverter + Reporter + OutboxRelay + DLQ.Consumer).
- **Proxy:** 1 (third-party).

The 4-channel ingestion split was chosen by the user for crash isolation. Two reviewers argued for collapsing it into 1 Ingestion service — surfaced as an open trade-off rather than silently overriding the user's choice.

## What's removed from the current system

- Temporal (workflows, signals, activities)
- SHA256 compute and verify
- `.tmp` + rename on the proxy outbox (replaced by staging-subdir + atomic `mv` to the watched dir using the final filename)
- Global "wait for all chunks of a package" before processing
- Repack of nested archives on NetworkB (assembled files placed flat per their relative paths)
- NACK+requeue for chunks-before-manifest (replaced by `inbox` buffering + inline drain)
- Sweeper-only crash recovery for post-COMMIT publish gaps (replaced by transactional outbox)
- Per-chunk holding files in NetworkB work dir (chunks read in-place from proxy delivery dir during concat)
- Single big manifest (replaced by job header + per-file mini-manifests)
- `ManifestPending` job state (no more single last-writer coordination)
- ManifestSweeper (per-file write + heartbeat sweeper covers recovery)

## Open questions still needing client input

See [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md). Headline items:

1. Confirm proxy-outbox safe-write strategy (staging-subdir + mv vs. proxy debounce).
2. Average files-per-job (drives year-2 partitioning + ingestion-split trade-off).
3. Storage retention SLA per volume.
4. ~~Converter cardinality~~ — ✅ answered **1→1**; `converted_file` collapsed into `source_file`.
5. Confirm the 4-channel ingestion split is the desired ops surface.
6. Local-PV vs. S3 storage default.

All in-document `[NEED CLARIFICATION]` markers from the previous revision are resolved (see ARCHITECTURE.md §10 resolutions table).

## Deliverables in this folder

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — full design, schemas, flow contracts, retry policy.
- [`MICROSERVICES.md`](MICROSERVICES.md) — per-service responsibilities, queues, scale strategy.
- [`SUMMARY.md`](SUMMARY.md) — this file.
- [`QUESTIONS-TO-CLIENT.md`](QUESTIONS-TO-CLIENT.md) — items needing client decision.
- [`architecture-v3.drawio`](architecture-v3.drawio) — service topology.
- [`cas-heartbeat-recovery.drawio`](cas-heartbeat-recovery.drawio) — normal flow, sweeper crash recovery, outbox post-COMMIT recovery.
- [`chunk-flow.drawio`](chunk-flow.drawio) — chunk-before-manifest buffering and inline drain.
- [`worker-exception-handling.drawio`](worker-exception-handling.drawio) — transient/permanent failure paths and job-level Failed resolution.
