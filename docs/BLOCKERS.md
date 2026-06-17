# Architecture Blockers — Pre-Development Review

Reviewed files: `ARCHITECTURE.md`, `MICROSERVICES.md`, `DB-SCHEMA.md`, `SUMMARY.md`, `QUESTIONS-TO-CLIENT.md`, `architecture-v3.drawio`, `chunk-flow.drawio`, `cas-heartbeat-recovery.drawio`, `worker-exception-handling.drawio`.

---

## CRITICAL — Will produce bugs if not fixed before coding

### B1. Proxy message has phantom `fileType` field ✅ RESOLVED
`architecture-v3.drawio` shows: `Rabbit {filePath, fileType, timestamp}` with `fileTypes: chunk | job-header | file-manifest | error-sentinel`.
`ARCHITECTURE.md §4` frozen contract is `{ filePath }` only — **no `fileType`, no `timestamp` field**.
ProxyListener classifies by parsing the `filePath` filename. Developer reading the diagram implements a listener expecting a `fileType` field. Wrong contract; frozen and cannot change.
**Fix:** Remove `fileType` from diagram label. Add note: "classification is by filename pattern, not message field."

---

### B2. FileManifestError before job-header → FK violation (unhandled case) ✅ RESOLVED (Option 1: buffer + drain)
`chunk-flow.drawio` Sub-flow A and `ARCHITECTURE.md §5.3` Branch ⑥ show Assembly.Worker doing:
```sql
INSERT INTO expected_file(job_id=..., status='Failed', ...) ON CONFLICT DO NOTHING;
```
`expected_file.job_id` is a FK to `job(id)`. If the proxy delivers `.file.ERROR.txt` before `.job.json`, no `job` row exists on Network B → FK violation → unhandled exception → message goes to DLQ with no recovery.

Chunks and file-manifests both have buffering logic (`inbox.published=FALSE`). FileManifestErrors have none.

**Fix options:**
- Buffer `FileManifestError` in `inbox` (kind=`FileManifestError`, published=FALSE) when no job row exists. Drain when job-header arrives (same as file-manifest buffer pattern).
- OR accept the risk if proxy guarantees job-header always arrives before any per-file events (confirm with proxy operator).

---

### B3. Transient-exhausted DLQ consumer skips mini-manifest write (Network A) ✅ RESOLVED
`worker-exception-handling.drawio` left path (transient exhaustion, node `l5`):
```
DLQ consumer: UPDATE expected_file.status='Failed'
```
No mini-manifest write shown.

`ARCHITECTURE.md §3.5` states: "A DLQ consumer flips the affected `source_file.status='Failed'` **and also writes the per-file mini-manifest** (with `status='Failed'`, `chunks=[]`) to the manifest outbox dir **before** committing the terminal status to the DB — the same write-before-commit sequencing as the inline permanent-failure path."

If transient-exhausted DLQ consumer skips manifest write, Network B never learns about the file → Assembly waits forever → job hangs until TimeoutSweeper.

**Fix:** Update diagram left-path DLQ consumer to match right-path DLQ consumer (`r-dlq`): check `manifest_written=FALSE`, write mini-manifest, then commit. Update `ARCHITECTURE.md §3.5` to explicitly state both DLQ paths (transient-exhausted AND permanent) follow write-before-commit.

---

## HIGH — Correctness risk at production load

### B4. `job.files_split_count` — orphaned schema column ✅ RESOLVED (deleted)
Column exists in `ARCHITECTURE.md:95` and `DB-SCHEMA.md:63`. Never incremented or read in any documented flow. `files_manifest_written_count` is the real gate for `AllManifestsWritten`.
**Fix:** Either document when/where it increments and what it gates, or delete the column.

---

### B5. `PartiallyDone` job status — never set by any flow ✅ RESOLVED (wired up)
Declared in `DB-SCHEMA.md` job status enum, referenced in `MICROSERVICES.md:44`. No code path in `ARCHITECTURE.md` ever assigns it. All Reporter completion paths produce `Done`, `Failed`, or `TimedOut`.
**Fix:** Either document which Reporter branch sets `PartiallyDone` and under what condition (vs `Done` with some files failed), or delete the status and use `Done` uniformly.

---

### B6. Sweeper publishes directly to Rabbit — contradicts transactional outbox design ⛔ OBSOLETE (superseded by S1)
~~`cas-heartbeat-recovery.drawio` shows sweeper → direct Rabbit arrow.~~
**Obsolete:** the per-phase heartbeat sweeper was removed entirely in **S1** (done-states + broker redelivery). There is no per-phase sweeper to publish from, so the contradiction no longer exists. Original analysis kept below for traceability.

> ~~If developer implements sweeper with direct Rabbit publish (as drawn), the transactional guarantee is lost. Sweeper can reset DB row, crash before publishing, row is permanently stranded. Fix: sweeper inserts into `outbox` table, not directly to Rabbit.~~

---

### B7. `manifest_written=TRUE` shown on `expected_file (B)` — column doesn't exist ✅ RESOLVED
`worker-exception-handling.drawio` node `r1`:
```
UPDATE source_file (A) / expected_file (B) SET manifest_written=TRUE, status='Failed'
```
`manifest_written` column exists **only** on `source_file` (Network A). `expected_file` (Network B) has no such column per `DB-SCHEMA.md`.
**Fix:** Split the diagram into Network A path (writes mini-manifest, has `manifest_written`) and Network B path (no manifest write, no `manifest_written` column). Or add a note clarifying the column only applies to Network A rows.

---

### B8. JobHeaderError `inbox` rows — no cleanup path ✅ RESOLVED (unified inbox-cleanup cron §6.6)
`chunk-flow.drawio` Sub-flow B states: "Stranded inbox rows cleaned by TimeoutSweeper (30 min)."
`ARCHITECTURE.md §5.7` TimeoutSweeper queries `job WHERE status IN ('Awaiting','Assembling')`. If job-header permanently failed, **there is no `job` row on Network B**. TimeoutSweeper never touches those `inbox` rows. Rows accumulate indefinitely.
**Fix:** Either add a dedicated `inbox` orphan-cleanup cron (scan `inbox WHERE kind='JobHeaderError' AND received_at < now() - interval '30 minutes'`), or document the exact mechanism by which the TimeoutSweeper cleans rows with no matching `job` row.

---

### B9. Assembly large-tx (delayed job-header) — no cap, no timeout ✅ RESOLVED (batch + loop, DRAIN_BATCH=500)
Both `chunk-flow.drawio` swimlane ② and `ARCHITECTURE.md §5.3` note risk: "tx can be large when job-header was delayed and many file-manifests and chunks have buffered. This risk is accepted; add a code comment."

No row cap, no Postgres `statement_timeout`, no circuit breaker documented. At 1M jobs/month with proxy delay scenarios this can hold locks for seconds and cascade.
**Fix:** Define a max drain batch size per transaction (e.g. 500 file-manifests per tx, loop if more). Or set a statement_timeout with retry. Document the chosen approach.

---

## MEDIUM — Schedule risk / development start blocker

### B10. Three client questions that change schema or design ⏳ MOSTLY RESOLVED — Q11/Q15/Q16 answered; Q12 owned (fix in progress)
Questions sharpened in `QUESTIONS-TO-CLIENT.md` §11/§12/§15/§16. **Q11 ✅ answered: 1→1** — `converted_file` collapsed into `source_file`, `chunk` keyed by `source_file_id` (resolves N3). **Q15/Q16 ✅ answered (2026-06-14, interim):** assume RWX for all shared dirs (every pod read/write-many to all folders), no node-affinity pinning; combined with **Q10 ✅ (no S3)** all volumes are filesystem-backed. **Q12 ⏳ owned/in-progress:** the transform/split implementer is making the Aspose PDF→DOCX converter deterministic (option a); persist-first-output kept as contingency (option b). Determinism sign-off still required per converter before it goes live.

Must answer before building:

| Q# | Question | Impact if wrong |
|----|----------|-----------------|
| Q11 ✅ | Converter cardinality: 1→1 vs 1→N? **Answered: 1→1** — tables collapsed, N3 resolved | Could collapse `source_file`+`converted_file` into one table |
| Q12 ⏳ | Converter determinism? **Owned/in-progress** — deterministic fix (a) + persist-first-output contingency (b) | Non-deterministic converter → mixed-version chunk assembly on retry → silent data corruption on Network B |
| Q15/Q16 ✅ | RWX StorageClass available? **Answered (interim): assume RWX everywhere**, no S3 (Q10) | Changes all PV strategy from day one |

Q12 remains the critical one: if any converter uses random seeds, current timestamp, or session IDs, the write-before-commit + idempotent re-split guarantee breaks silently. Confirmed non-deterministic so far: Aspose PDF→DOCX (embeds `rsid` + timestamps). Now owned by the transform/split implementer; each converter needs determinism sign-off before going live.

---

### B11. `inbox` table retention/cleanup undocumented ✅ RESOLVED (unified inbox-cleanup cron §6.6)
`ARCHITECTURE.md §6.6` covers work dirs, proxy delivery dir, Postgres `job/chunk` rows. `inbox` rows (both `published=TRUE` terminal rows and orphaned rows) are never scheduled for deletion.
At 1M jobs/month this becomes a table-scan and disk problem.
**Fix:** Add `inbox` to the §6.6 retention policy. Proposed: DELETE `inbox` rows WHERE `received_at < now() - N days` (all kinds; job is terminal by then).

---

### B12. Rabbit `definitions.json` not provided
Topology described textually (quorum queues, DLX, TTL, `x-max-length`, policies). No actual `definitions.json` files in `rabbit/network-a/` or `rabbit/network-b/`. Every developer sprint starts with "spin up local env" — they hand-craft topologies that diverge from prod.
**Fix:** Author `definitions.json` as part of pre-sprint setup. Confirm topology with MICROSERVICES.md queue mapping table.

---

### B13. No initial migration files
Schema in `DB-SCHEMA.md` and `ARCHITECTURE.md §3.2/§5.2` is complete. But `db/network-a/migrations/` and `db/network-b/migrations/` directories referenced in `ARCHITECTURE.md §9` repo layout don't exist. First sprint has no runnable DDL.
**Fix:** Author `0001_initial.sql` for both networks from the documented schemas before sprint 1.

---

## Summary Table

| # | Severity | Status | Issue | Source conflict |
|---|----------|--------|-------|-----------------|
| B1 | CRITICAL | ✅ Resolved | Phantom `fileType` in proxy Rabbit message | `architecture-v3.drawio` vs `ARCHITECTURE.md §4` |
| B2 | CRITICAL | ✅ Resolved | FileManifestError before job-header → FK violation, no buffer | `chunk-flow.drawio` + `ARCHITECTURE.md §5.3` |
| B3 | CRITICAL | ✅ Resolved | Transient DLQ consumer skips mini-manifest write on Network A | `worker-exception-handling.drawio` vs `ARCHITECTURE.md §3.5` |
| B4 | HIGH | ✅ Resolved | `files_split_count` orphaned schema column | `DB-SCHEMA.md` + `ARCHITECTURE.md` |
| B5 | HIGH | ✅ Resolved | `PartiallyDone` status never set by any flow | `DB-SCHEMA.md` + `MICROSERVICES.md` |
| B6 | HIGH | ⛔ Obsolete (see S1) | Sweeper shown publishing direct to Rabbit (not via outbox) — moot: per-phase sweeper removed | `cas-heartbeat-recovery.drawio` vs `ARCHITECTURE.md §3.3` |
| B7 | HIGH | ✅ Resolved | `manifest_written` shown on `expected_file (B)` — column doesn't exist | `worker-exception-handling.drawio` vs `DB-SCHEMA.md` |
| B8 | HIGH | ✅ Resolved | JobHeaderError inbox rows — no cleanup path | `chunk-flow.drawio` + `ARCHITECTURE.md §5.7` |
| B9 | HIGH | ✅ Resolved | Assembly large-tx no cap or timeout | `chunk-flow.drawio` + `ARCHITECTURE.md §5.3` |
| B10 | MED | ⏳ Mostly resolved | Q11/Q15/Q16 answered; Q12 owned/in-progress (determinism sign-off pending) | `QUESTIONS-TO-CLIENT.md` |
| B11 | MED | ✅ Resolved | `inbox` table retention undocumented | `ARCHITECTURE.md §6.6` |
| B12 | MED | ☐ Open | No `definitions.json` for Rabbit topology | `MICROSERVICES.md` |
| B13 | MED | ☐ Open | No initial DB migration files | `ARCHITECTURE.md §9` |

---

## Second-pass review — additional findings (N-series)

Found during the post-fix dev-readiness review.

| # | Severity | Status | Issue | Source conflict |
|---|----------|--------|-------|-----------------|
| N1 | HIGH | ✅ Resolved | `cleaned_at` column missing from Network B `job` schema | `DB-SCHEMA.md` / `ARCHITECTURE.md §5.2` vs `§6.6` + `MICROSERVICES.md` |
| N2 | HIGH | ✅ Resolved | `updated_at` column missing from Network B `job` schema | `DB-SCHEMA.md` / `ARCHITECTURE.md §5.2` vs `§5.6`/`§5.7` |
| N3 | CRITICAL | ✅ Resolved | Chunk filename collision if 1→N — Q11 answered **1→1**, tables collapsed, `chunk` keyed by `source_file_id`; collision impossible | `ARCHITECTURE.md §3.4` + `QUESTIONS §11` |
| N4 | MED | ✅ Resolved | `inbox.job_id` "NULL for early chunks" contradicts drain-by-`job_id` | `DB-SCHEMA.md` vs `ARCHITECTURE.md §5.2/§5.3` |
| N5 | MED | ✅ Resolved | DLQ consumer referenced in flows but not a declared service | `ARCHITECTURE.md §3.5/§5.8` vs `MICROSERVICES.md` |
| N6 | MED | ✅ Resolved (doc) | Reporter callback is at-least-once, doc claimed exactly-once → corrected; client idempotency required (QUESTIONS §3) | `ARCHITECTURE.md §5.6` vs `MICROSERVICES.md:45` |
| N7 | LOW | ✅ Resolved | Stability check moot — proxy publishes Rabbit msg only after move; NB never sees mid-write file (Q1/§18.1) | `ARCHITECTURE.md §5.9` + `QUESTIONS §1` |
| N8 | LOW | ☐ Open | ProxyListener: undefined handling for unparseable filename / missing file | `ARCHITECTURE.md §5.3` |
| N9 | MED | ☐ Open | `byte_length` removed in QUESTIONS §18 but still present in ARCH §3.6 / DB-SCHEMA / SUMMARY #10 — cross-doc contradiction; coupled to open §18.2–.4 | `QUESTIONS §18` vs `ARCHITECTURE/DB-SCHEMA/SUMMARY` |
| R1 | HIGH | ✅ Resolved | `consumer_timeout` / queue type — RabbitMQ 3.13 + quorum queues pinned; classic removed (see R6/Q20) | `ARCHITECTURE.md §3.3` / `MICROSERVICES.md` |
| R2 | MED | ✅ Resolved | `consumer_timeout` 5-min floor undocumented — calibration could target sub-5-min values that the broker ignores | `ARCHITECTURE.md §3.3` / `MICROSERVICES.md` |
| R3 | HIGH | ✅ Resolved | DLX hops are at-most-once by default — silent message loss when target queue full or unavailable; fixed with `dead-letter-strategy=at-least-once` policy | `ARCHITECTURE.md §3.3` / `§3.5` |
| R4 | HIGH | ✅ Resolved | `reject-publish-dlx` on terminal queues circular/undefined — replaced with `reject-publish` + large `x-max-length` on `*.dead` | `ARCHITECTURE.md §3.5` / `MICROSERVICES.md` |
| R5 | MED | ✅ Resolved | 36 queues when 16 suffice — replaced per-queue retry/dead pairs with shared retry/dead + reroute exchange per network | `ARCHITECTURE.md §3.5` / `MICROSERVICES.md` |

### N3. Chunk filename collision if converter is 1→N (CRITICAL — ✅ RESOLVED: Q11 = 1→1)
**Resolved.** Client confirmed Q11 = **1→1**. `converted_file` collapsed into `source_file`; `chunk` is now keyed by `source_file_id` with `UNIQUE (job_id, source_file_id, index)`. One converted file per source file → `index` cannot repeat for a `source_file_id` → no filename collision. Original analysis kept below for context.

Chunks are named `{job_id}_{source_file_id}_chunk_{index}` (§3.4 step 4) with **no `converted_file_id`**. The `chunk` table UNIQUE is `(job_id, converted_file_id, index)`, so the DB tolerates two converted files of one source file sharing index 0 — but their **filenames are identical**, so `inbox.file_path` PK collides on Network B and one chunk silently overwrites the other → corrupt assembly. The entire chunk-naming + per-source-file mini-manifest scheme assumes 1→1. **Q11 is therefore a correctness precondition, not a table-collapse optimization.**
**Fix (after Q11):** if 1→N is real, include `converted_file_id` in the chunk filename (`{job_id}_{source_file_id}_{converted_file_id}_chunk_{index}`) and aggregate per-source-file completion across all converted files before writing the mini-manifest. If 1→1 confirmed, document the invariant explicitly.

### N7. Stability check — synchronous 200ms (LOW — ✅ RESOLVED via Q1)
`§5.9` two-stat check (`stat` → sleep 200ms → `stat`) blocks the consumer per file and assumes 200ms suffices. **Resolved (2026-06-14):** the proxy publishes its Rabbit message to NB *only after the file has finished moving* into the proxy delivery dir, so NB acts on the message and never observes a mid-write file. The two-stat check is now belt-and-suspenders, not a correctness dependency — it can stay (cheap) or be dropped. No size-validation against `byte_length` is needed (and `byte_length` is contested — see N9).

### N9. `byte_length` cross-doc inconsistency (MED — ☐ Open, deferred to §18.2–.4)
`QUESTIONS §18` records a design decision that `byte_length` was **removed** from the chunk manifest protocol (the proxy transform changes file size non-deterministically, so NB can't validate observed-vs-manifest size). But `byte_length` still appears in `ARCHITECTURE.md §3.6` (mini-manifest `byteLength`), `DB-SCHEMA.md` (`chunk.byte_length`, `expected_chunk.byte_length`), and `SUMMARY.md` choice #10 ("Manifest carries `byte_length`"). The same §18 also leaves "proxy ships each file unmodified" (ARCH §4) unreconciled with "proxy transforms non-deterministically" (§18). These are one open question.
**Fix:** resolve as a single unit once the proxy-transform questions (§18.2–.4) are answered — either remove `byte_length` end-to-end (if the non-deterministic, size-changing transform stands) or reinstate it in QUESTIONS §18 (if the transform is in fact size-preserving / absent). Do not half-change now.

### N8. ProxyListener — undefined edge branches (LOW)
No documented handling for: (a) a filename matching none of the 7 patterns (e.g. unparseable `job_id`), or (b) a proxy message whose `filePath` no longer exists on disk. Likely → unhandled exception → DLQ with no recovery path.
**Fix:** define a fallback branch (audit-log + ack, or route to a `proxy.unclassified` dead queue) and a missing-file check (treat as transient retry, then audit-drop).

---

## Simplifications (S-series)

Deliberate complexity-reduction decisions taken after the dev-readiness review, with their rationale and ripple effects.

### S1. Remove per-row heartbeat + per-phase sweeper + intermediate `-ing` states ✅ APPLIED (docs)

**Decision.** Replace the "per-row status CAS + 10s heartbeat loop + per-phase sweeper (30s threshold, 60s poll)" recovery layer with **done-states only + RabbitMQ redelivery + `consumer_timeout`**. Each step does idempotent work, then a single done-CAS (`WHERE status='Prev'`) that also gates the outbox insert / counter increment.

**Why.** Three overlapping recovery mechanisms (broker redelivery, heartbeat-sweeper, outbox) collapsed to two. The heartbeat-sweeper only *uniquely* helped the rare "app deadlock with live connection" case, and only by reducing latency — never correctness. Verified against RabbitMQ docs:
- Unacked deliveries are **auto-requeued when the channel/connection closes** ([confirms](https://www.rabbitmq.com/docs/confirms)) → crash recovers in **seconds**.
- AMQP **connection heartbeat** default 60s, dead after 2 missed ([heartbeats](https://www.rabbitmq.com/docs/heartbeats)) → network death ~**60s**.
- **`consumer_timeout`** default 30 min closes the channel and requeues for a stuck-but-connected consumer ([consumers](https://www.rabbitmq.com/docs/consumers)) → deadlock recovery, tunable.
- At-least-once + **idempotent consumers are mandatory regardless** ([confirms](https://www.rabbitmq.com/docs/confirms)), so no new idempotency burden is added.

Crucially, crash recovery is now **faster** than the old ~90s sweeper: with no `-ing` row to block it, the redelivered message reprocesses immediately. Only the deadlock case regresses (to `consumer_timeout`), and it is backstopped by the job-level `TimeoutSweeper` (B) and the `files_manifest_written_count` counter + per-file DLQ-terminal (A).

**Trade-offs accepted.** (1) Deadlock recovery = `consumer_timeout` instead of ~90s — tunable, SLA-backstopped. (2) A `consumer_timeout` requeue can run a second worker concurrently with a slow-but-healthy first worker → duplicate work (idempotent, harmless; only the done-CAS winner publishes). (3) Loss of the in-DB "who's processing what" view — replaced by logs/traces + the `redelivered`-rate metric. (4) Mitigations: `prefetch=1` (caps timeout-requeue blast radius), quorum `delivery-limit=20` (poison backstop), `consumer_timeout` sized > max legit step time.

**Ripple effects (all applied in docs).**
- `ARCHITECTURE.md`: §1 principle 3, §3.1–§3.4 (Prepare/Convert/Split flows), §3.3 worker contract + new recovery/calibration block, §3.5 DLQ idempotency, §5.1/§5.5 ReverseConverter, §5.2 schemas, §6.1–§6.4 + §6.9 checklist, §7 removed-table, §10 decisions 2 & 10.
- `DB-SCHEMA.md`: dropped `worker_id`/`last_heartbeat_at`/`*_stale_hb` from `job`/`source_file`/`expected_file` (A+B); dropped `manifest_written` from `source_file`; `phase_config` reduced to `max_retries`; status enums reduced to done-states.
- `MICROSERVICES.md`: service-type cells → "Worker (done-CAS)"; "Heartbeat / Sweeper Mechanics" replaced by "Recovery & Calibration"; `phase_config` infra row + new RabbitMQ-recovery-settings row.
- `SUMMARY.md`: headline choices 1 & 14, removed-list.
- `QUESTIONS-TO-CLIENT.md`: new §19 (calibration inputs).
- Diagrams: `cas-heartbeat-recovery.drawio` reworked (sweeper path → broker-redelivery path); `worker-exception-handling.drawio` heartbeat/`manifest_written` nodes updated.

**`consumer_timeout` calibration is a pre-launch task,** not a blocker: ship with the 30-min default, set the real value from the per-step duration histogram after ~1 week. Driving client inputs in `QUESTIONS-TO-CLIENT.md` §19.

---

### S2. Client answers applied (2026-06-14) ✅ APPLIED (docs)

**Decision.** Second batch of client answers (QUESTIONS §1, 3, 4, 5, 7, 8, 10, 12, 13, 15, 16, 17, 18.1) resolved/reversed several design points:
- **Safe-write reverted to `.tmp`+rename everywhere** — the proxy ignores `.tmp` files, so the staging-subdir+`mv` workaround is dropped (§1). Applies to A→proxy outbox (data + manifest), local files, B `target_path`, CSV.
- **Proxy delivery atomicity resolved** — the proxy publishes its Rabbit message only *after* the file finishes moving; NB acts on the message, never observes a mid-write file (§1/§18.1). Two-stat stability check (§5.9) downgraded to belt-and-suspenders (resolves N7).
- **Chunk handling** — Assembly reads chunks in-place during concat then best-effort **deletes consumed chunks post-COMMIT**; Reporter sweeps any remainder on terminal; the proxy self-cleans days-old leftovers (replaces "Reporter deletes all proxy chunks on terminal job").
- **Storage** — RWX assumed for all shared dirs (interim; every pod read/write-many to all folders), no node-affinity pinning; **no S3 available** → filesystem-only (§10/§15/§16). Resolves the PV-strategy half of B10.
- **Nested-archive output path** — each archive container `name.ext` → directory `name_ext` (replace last dot with underscore), leaf keeps its real extension; baked into `original_relative_path` at A; B uses it verbatim (§5).
- **target_path** — NFS/SMB network share, direct pod write, `.tmp`+rename, consumer ignores `.tmp`, collisions overwrite (last-writer-wins) (§17).
- **Dedup** — first-writer-wins (`inbox.file_path` PK + `ON CONFLICT DO NOTHING`); deterministic re-split makes re-sent chunks byte-identical, so no overwrite rule is needed.
- **Determinism (§12)** — owned by the transform/split implementer; option (a) deterministic Aspose fix in progress, option (b) persist-first-output kept as contingency.
- **Confirmed (no change):** 4-channel ingestion (§7), at-least-once callback + idempotent consumer (§3), persist payload JSONB (§4), single chunk error → whole file `Failed` in CSV (§13).
- **SLA** — 30-min kept (client says avg job ~5 min excl. proxy delay; no files-per-job data → partitioning + `consumer_timeout` sizing remain post-launch, §8).

**Ripple effects (applied in docs).**
- `ARCHITECTURE.md`: §1 principle 5, §3.3/§3.4, §4–§4.2, §5.1/§5.3/§5.5/§5.9, §6.5/§6.6, §7, §10.
- `MICROSERVICES.md`: Prepare/Split/Proxy/Assembly/ReverseConverter/Reporter rows + Storage row.
- `DB-SCHEMA.md`: `inbox`/`expected_chunk`/`original_relative_path` notes, Resolved-decisions + Pattern-summary.
- `SUMMARY.md`: pipeline-in-one-breath, headline choices 5/12, removed-list, open-questions.
- `QUESTIONS-TO-CLIENT.md`: §1, 3, 4, 5, 7, 10, 13, 15, 16, 17 marked ✅; §18.1 resolved (18.2–.4 open); §12 owned; §8 noted.
- Diagrams: `cas-heartbeat-recovery.drawio` (`.tmp+rename` label), `chunk-flow.drawio` (post-COMMIT chunk delete + in-place banner), `architecture-v3.drawio` (proxy publishes-after-move note).

**Still open after this batch:** §9 retention SLA, §8 files-per-job data, §12 determinism verification (owned), §18.2/.3/.4 proxy transform, §2/§19/§20 retry & RabbitMQ. Plus **N9** (`byte_length` cross-doc inconsistency, coupled to §18).

---

### S3. Per-`callingSystemId` configuration + manifest-carried reverse conversion (2026-06-17) ✅ APPLIED (docs)

**Decision.** Third client batch (QUESTIONS §21). All caller-varying config is keyed by `callingSystemId` with a `default` fallback:
- **Network A `conversion_rule`** (`(callingSystemId, source_format)`): forward conversion, reverse conversion, and the Split file-size limit (`FileSizeLimitMb` = max chunk size — a **splitting** parameter, not an ingestion reject limit). Example: `{SourceFormat:heic, RequiredConversion:PNG, reverseConversion:heic, FileSizeLimitMb:200}`. Precedence `(sys,fmt)→(sys,default)→(default,fmt)→(default,default)`.
- **`calling_system_config`** (both networks): per-caller folders + job SLA. **SLA independent per network** (no A→B carry); operator keeps the two aligned per `callingSystemId`.
- **Reverse conversion is no longer the inverse of the forward conversion.** It is an independent target resolved on A (may be `null`=skip, equal the source format, or a different type), carried to B verbatim in the mini-manifest's `reverseConversion` field. Network B keeps **no** conversion config; ReverseConverter obeys the carried value.
- **Priority by `callingSystemId` deferred** — RabbitMQ 3.13 quorum has no native message priority (4.0 only); client will discuss a 4.x upgrade first. `calling_system_config` reserves a `priority` column.

**Schema changes.** New tables `calling_system_config` (A+B) and `conversion_rule` (A only). New columns: `source_file.reverse_conversion`, `source_file.file_size_limit_mb` (A); `expected_file.reverse_conversion` (B). Mini-manifest gains a `reverseConversion` field (§3.6). Old `config` key/value keys `proxy_rule.<format>` / `converter.<format>` / `timeout_sla_minutes` retired into the new tables.

**Ripple effects (applied in docs).**
- `ARCHITECTURE.md`: §3.1 (Convert/Split rows), §3.2/§5.2 (table lists), §3.4 (Convert/Split flow), §3.6 (mini-manifest `reverseConversion`), §3.7 + §5.7 (per-caller SLA in sweeper SQL), §5.1 (Assembly + ReverseConverter rows), §5.5 (reverse-conversion rewrite), §6.1 (full configuration rewrite), §10 (resolutions #20–#22).
- `DB-SCHEMA.md`: intro bullets, new `calling_system_config`/`conversion_rule` DDL, `source_file`/`expected_file` columns, Resolved-decisions + Pattern-summary.
- `MICROSERVICES.md`: Convert/Split/Assembly/ReverseConverter rows, defaults note, shared-infra config row.
- `SUMMARY.md`: pipeline-in-one-breath, headline choices 16/17, open-questions #6.
- `QUESTIONS-TO-CLIENT.md`: new §21.

**Diagrams not yet updated** (per instruction — `.drawio` untouched this pass). Follow-up: `architecture-v3.drawio` (config source note), `chunk-flow.drawio` / `worker-exception-handling.drawio` (mini-manifest now carries `reverseConversion`; ReverseConverter reads `reverse_conversion`, not inverse-of-applied).

**Still open after this batch (unchanged):** §9 retention, §8 files-per-job, §12 determinism, §18.2/.3/.4 proxy transform, §2/§19/§20; **N9** `byte_length`. **New deferred item:** job priority (pending RabbitMQ 4.x, §21).

---

## RabbitMQ review — R-series

Found during RabbitMQ docs alignment check against `ARCHITECTURE.md` and `MICROSERVICES.md`. All resolved in the same pass.

| # | Severity | Status | Issue |
|---|----------|--------|-------|
| R1 | HIGH | ✅ Resolved | `consumer_timeout` / queue type → RabbitMQ 3.13 + quorum (R6) |
| R2 | MED | ✅ Resolved | `consumer_timeout` 5-min floor |
| R3 | HIGH | ✅ Resolved | DLX at-most-once silent loss |
| R4 | HIGH | ✅ Resolved | `reject-publish-dlx` circular on terminal queues |
| R5 | MED | ✅ Resolved | 36 queues → 16 with shared retry/dead |
| R6 | MED | ✅ Resolved (Q20) | Queue type pinned: RabbitMQ 3.13, quorum, 3-node cluster; classic removed |

### R1. `consumer_timeout` enforcement & queue type ✅ RESOLVED (quorum on 3.13 — see R6/Q20)

On RabbitMQ 4.3+, `consumer_timeout` is enforced **only on quorum queues**; on the pinned **3.13** it is evaluated channel-side and applies to all queue types. Either way the deployment uses **quorum queues exclusively** (QUESTIONS §20), so `consumer_timeout` is always enforced and the classic-enforcement question is moot. Classic queues are not used.
**Applied:** all main/retry/dead queues are quorum. `ARCHITECTURE.md §3.3` calibration point 1 + `MICROSERVICES.md` shared-infra row. The client-side watchdog is kept as the *faster* primary deadlock reclaim (not a classic-queue crutch); see R6.

---

### R2. `consumer_timeout` 5-minute floor undocumented ✅ RESOLVED

RabbitMQ evaluates `consumer_timeout` at **1-minute intervals** and its docs explicitly discourage values below 5 minutes. The calibration formula "3–4× max step time" can produce a target below 5 minutes for fast phases (e.g. 10s × 4 = 40s), which the broker effectively ignores. This creates a false sense that the timeout is tightly tuned when it is not.
**Fix:** Added minimum-5-minutes floor to calibration guidance in `ARCHITECTURE.md §3.3` point 1 and `MICROSERVICES.md` Recovery & Calibration section.

---

### R3. DLX hops are at-most-once — silent message loss ✅ RESOLVED

RabbitMQ dead-lettering is at-most-once by default: messages are removed from the source queue and re-published to the DLX target **without publisher confirms**. If the target queue is full, offline, or has no quorum, the message is silently dropped. In this architecture, every transient failure does 2 DLX hops (`main → retry`, `retry → main` or `retry → dead`). A dropped message means the file never reaches `Failed`, the `files_manifest_written_count` counter never increments, and Network B hangs until `TimeoutSweeper` fires 30 minutes later.

Quorum queues support **at-least-once dead-lettering** (publisher confirms used internally) — it just requires explicit configuration.
**Fix:** Added calibration point 6 to `ARCHITECTURE.md §3.3`: apply `dead-letter-strategy = at-least-once` policy to all quorum queues. Added to `MICROSERVICES.md` shared infrastructure row.

---

### R4. `reject-publish-dlx` on terminal queues circular/undefined ✅ RESOLVED

`reject-publish-dlx` dead-letters the *newly rejected overflow message* to the queue's DLX. On `*.dead` queues, no further DLX is defined — either the message is silently dropped (no DLX configured) or a tertiary dead-letter chain forms (DLX configured, but again at-most-once per R3). Neither is the intended behavior.
**Fix:** `reject-publish-dlx` never used anywhere. All queues including `*.dead` use `reject-publish`. `*.dead` queues get a large `x-max-length` (e.g. 100 000) so they never fill under normal operation; alert on depth. Applied to `ARCHITECTURE.md §3.5` backpressure bullet and `MICROSERVICES.md` shared infrastructure row.

---

### R5. 36 queues when 16 suffice ✅ RESOLVED

Per-queue `*.retry` + `*.dead` pairs produce 12 retry + 12 dead = 24 overhead queues on top of 12 main queues. Shared retry/dead pattern (one per network) achieves identical behavior with 2 retry + 2 dead = 4 overhead queues.

**Pattern:** one `retry` exchange (direct) → shared `retry` queue (TTL=60s, `x-dead-letter-exchange=reroute`, no `x-dead-letter-routing-key`) → `reroute` exchange (direct, bound to each main queue by routing key). Workers publish to `retry` exchange with routing key = original queue name; routing key preserved through TTL expiry → rerouted to the correct main queue. `x-death` header (built-in, set by RabbitMQ) replaces manual `x-retry-count`.

**Queue count:** Network A: 3 main + 1 retry + 1 dead = 5. Network B: 9 main + 1 retry + 1 dead = 11. Total: **16** (down from 36). Exchange count: 2 per network (retry + reroute) = 4 total.
**Fix:** Updated `MICROSERVICES.md` shared infrastructure RabbitMQ row and queue-mapping note. `ARCHITECTURE.md §3.5` backpressure bullet updated. `definitions.json` authoring (B12) should implement this topology.

---

### R6. Queue type ✅ RESOLVED — RabbitMQ 3.13, quorum, 3-node cluster (Q20, 2026-06-17)

**Context.** Three recovery backstops the design uses are quorum-queue features: `consumer_timeout` (hung-worker reclaim), `delivery-limit` (poison backstop), and at-least-once dead-lettering (no silent DLX loss). While the client's RabbitMQ version was unknown, the design also carried a client-side watchdog and a Network-A `TimeoutSweeper` (§3.7) as defense-in-depth.

**Resolution (2026-06-17).** Client pinned **RabbitMQ 3.13 + quorum queues** (PostgreSQL pinned **16** in the same decision). All classic-queue fallback content is **removed** from the architecture. The watchdog and `TimeoutSweeper-A` are **kept** — not as a classic-queue crutch, but because:
- **Client-side watchdog** (ARCHITECTURE.md §3.3) reclaims a hung-but-alive worker in seconds — *faster* than `consumer_timeout` (1-min eval, 30-min default). Its timeout feeds the app-level `max_retries=5` counter, parallel to the broker `delivery-limit`.
- **Network-A `TimeoutSweeper`** (ARCHITECTURE.md §3.7) is the job-level SLA backstop for residual modes: a watchdog-evading hang or a wedged dead-letter target leaving a `source_file` non-terminal. Co-located on an `OutboxRelay-A` replica (`SWEEPER_ENABLED=true`).

**3.13-specific config (verified vs the 3.13 docs):**
- `delivery-limit` has **no default on 3.13** (the default of 20 arrives in 4.0) → **set explicitly via policy to 20**.
- `consumer_timeout` enforced on quorum, default 30 min.
- `dead-letter-strategy=at-least-once` requires `overflow=reject-publish` (not `drop-head`) + a DLX — both already set.
- Mirrored classic queues deprecated in 3.13, removed in 4.0 — not used.

**Topology:** recommended **3-node cluster per network** (odd majority for quorum's HA guarantee; single node = broker SPOF — the one remaining open sub-item in Q20).
**Applied to docs:** `ARCHITECTURE.md` top stack note, §3.3 (queue-type + watchdog block, calibration 1/3/6), §3.7, §6.4, §6.9; `MICROSERVICES.md` intro + Network A note + Recovery & Calibration + shared-infra rows; `SUMMARY.md` choices 1/4/14 + open questions; `QUESTIONS-TO-CLIENT.md` §19/§20.
