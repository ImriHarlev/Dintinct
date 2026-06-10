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

### B6. Sweeper publishes directly to Rabbit — contradicts transactional outbox design ✅ RESOLVED
`cas-heartbeat-recovery.drawio` shows sweeper → direct Rabbit arrow.
`ARCHITECTURE.md §3.3` says: "for each returned id: insert outbox row to republish from the previous step's queue."

If developer implements sweeper with direct Rabbit publish (as drawn), the transactional guarantee is lost. Sweeper can reset DB row, crash before publishing, row is permanently stranded.
**Fix:** Update diagram to show sweeper inserts into `outbox` table, not directly to Rabbit. OutboxRelay publishes.

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

### B10. Three client questions that change schema or design ⏳ PARTIAL — Q11 answered, Q12/Q15/Q16 pending
Questions sharpened in `QUESTIONS-TO-CLIENT.md` §11/§12/§15/§16 — each tagged with a crisp yes/no question, an answer→impact table, and a default-if-unanswered. **Q11 ✅ answered: 1→1** — `converted_file` collapsed into `source_file`, `chunk` keyed by `source_file_id` (resolves N3). Still pending: **Q12** (determinism — silent-corruption risk), **Q15/Q16** (RWX StorageClass / PV strategy). Those need client sign-off.

Must answer before building:

| Q# | Question | Impact if wrong |
|----|----------|-----------------|
| Q11 ✅ | Converter cardinality: 1→1 vs 1→N? **Answered: 1→1** — tables collapsed, N3 resolved | Could collapse `source_file`+`converted_file` into one table |
| Q12 | Converter determinism? | Non-deterministic converter → chunk length mismatch on retry → silent data corruption on Network B |
| Q15/Q16 | RWX StorageClass available? | Changes all PV strategy from day one |

Q12 is especially critical: if any converter uses random seeds, current timestamp, or session IDs, the whole write-before-commit + idempotent re-split guarantee breaks silently.

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
| B6 | HIGH | ✅ Resolved | Sweeper shown publishing direct to Rabbit (not via outbox) | `cas-heartbeat-recovery.drawio` vs `ARCHITECTURE.md §3.3` |
| B7 | HIGH | ✅ Resolved | `manifest_written` shown on `expected_file (B)` — column doesn't exist | `worker-exception-handling.drawio` vs `DB-SCHEMA.md` |
| B8 | HIGH | ✅ Resolved | JobHeaderError inbox rows — no cleanup path | `chunk-flow.drawio` + `ARCHITECTURE.md §5.7` |
| B9 | HIGH | ✅ Resolved | Assembly large-tx no cap or timeout | `chunk-flow.drawio` + `ARCHITECTURE.md §5.3` |
| B10 | MED | ⏳ Awaiting client | 3 client questions block schema/design decisions (Q11, Q12, Q15/Q16) | `QUESTIONS-TO-CLIENT.md` |
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
| N7 | LOW | ☐ Open (client Q1) | Stability check 200ms synchronous — throughput + large-file robustness | `ARCHITECTURE.md §5.9` + `QUESTIONS §1` |
| N8 | LOW | ☐ Open | ProxyListener: undefined handling for unparseable filename / missing file | `ARCHITECTURE.md §5.3` |

### N3. Chunk filename collision if converter is 1→N (CRITICAL — ✅ RESOLVED: Q11 = 1→1)
**Resolved.** Client confirmed Q11 = **1→1**. `converted_file` collapsed into `source_file`; `chunk` is now keyed by `source_file_id` with `UNIQUE (job_id, source_file_id, index)`. One converted file per source file → `index` cannot repeat for a `source_file_id` → no filename collision. Original analysis kept below for context.

Chunks are named `{job_id}_{source_file_id}_chunk_{index}` (§3.4 step 4) with **no `converted_file_id`**. The `chunk` table UNIQUE is `(job_id, converted_file_id, index)`, so the DB tolerates two converted files of one source file sharing index 0 — but their **filenames are identical**, so `inbox.file_path` PK collides on Network B and one chunk silently overwrites the other → corrupt assembly. The entire chunk-naming + per-source-file mini-manifest scheme assumes 1→1. **Q11 is therefore a correctness precondition, not a table-collapse optimization.**
**Fix (after Q11):** if 1→N is real, include `converted_file_id` in the chunk filename (`{job_id}_{source_file_id}_{converted_file_id}_chunk_{index}`) and aggregate per-source-file completion across all converted files before writing the mini-manifest. If 1→1 confirmed, document the invariant explicitly.

### N7. Stability check — synchronous 200ms (LOW — relates to client Q1)
`§5.9` two-stat check (`stat` → sleep 200ms → `stat`) blocks the consumer per file and assumes 200ms suffices. Throughput: forces many ProxyListener replicas at peak. Robustness: a large chunk still being written on slow storage can pass a 200ms check while incomplete. Moot if the proxy delivers via atomic rename — confirm under Q1.
**Fix:** confirm proxy delivery semantics (Q1). If non-atomic, make the wait configurable and/or validate observed size against `expected_chunk.byte_length` before setting `received_at`.

### N8. ProxyListener — undefined edge branches (LOW)
No documented handling for: (a) a filename matching none of the 7 patterns (e.g. unparseable `job_id`), or (b) a proxy message whose `filePath` no longer exists on disk. Likely → unhandled exception → DLQ with no recovery path.
**Fix:** define a fallback branch (audit-log + ack, or route to a `proxy.unclassified` dead queue) and a missing-file check (treat as transient retry, then audit-drop).
