# Open Questions for the Client

These items need a client/stakeholder decision before final implementation.

---

## 1. Direct writes to the Proxy outbox (no `.tmp` + rename)  &nbsp;✅ ANSWERED — `.tmp` + rename is the chosen mechanism

> **Client answer (2026-06-14):** "We can do `.tmp` + rename on the proxy folders + destination folders; the proxy ignores `.tmp` files." **Applied:** `.tmp` + rename IS the chosen atomic-write mechanism on the A→proxy outbox dirs and on `target_path` (§17). The earlier premise (proxy may pick up temp-named files) is **reversed** — the proxy ignores `.tmp` files — so the staging-subdir + `mv` workaround (option **B**) is **dropped**. The mitigation options below (A–E) are superseded.
>
> **Related — proxy *delivery* atomicity (below, also §18.1): also RESOLVED.** The proxy publishes its RabbitMQ message to NetworkB **only after** the file finishes moving, so NetworkB acts on the message and never observes a mid-write file. This is the truncation guard that replaces the dead `byte_length` check.

**Context.** NetworkA writes chunks and the manifest JSON directly into the proxy outbox directories. We were told the proxy may pick up `.tmp` (or any temp-named) files, so the standard atomic-write trick (write to `.tmp`, then rename to the final name) cannot be used.

**Risk.** The proxy watches the outbox via the filesystem. If it sees the file before NetworkA finishes writing, it could ship a truncated file. With the default `FileSystemWatcher`-style behaviors on Windows/Linux, file-creation events fire on open, not on close, which means partial reads are possible.

**Mitigation options to choose from:**

- **A. Accept the risk.** Empirically the proxy is slow enough / debounces enough that this never bites in practice. *Recommended only if proxy operator confirms a debounce or post-stable check.*
- **B. Write to a staging sub-directory that the proxy does not watch, then `mv` (single-rename, no `.tmp` suffix) into the watched dir.** A `mv` within the same filesystem is atomic. The destination filename is the final name from first appearance.
- **C. Write to a separate filesystem and `mv` across.** Not atomic — same problem as direct writes.
- **D. Ask the proxy operator to ignore files until a sibling `<name>.ready` marker appears.** Requires proxy change.
- **E. Ship via the manifest path: write a per-file marker into the manifest outbox dir only after the chunk file is fully closed.** Requires proxy logic change.

**Question.** Which mitigation is acceptable, or does the proxy already debounce on inactivity? Option **B** is cheapest on our side if confirmed compatible.

**Related — proxy *delivery* atomicity on the B side (BLOCKERS N7).**  ✅ **RESOLVED (2026-06-14).** The proxy publishes its RabbitMQ message to NetworkB **only after** the file finishes moving into B's delivery dir. NetworkB acts on the message, never scans the directory mid-write, and so can never observe a truncated file. This message-gated delivery is the truncation guard. **NetworkB does NOT validate chunk size against `byte_length`** — assembly is count-gated and concat proceeds regardless of any size difference; `byte_length` is carried as metadata/audit only. *(NB is gated on the post-move Rabbit message, so it never observes a mid-write file regardless.)*

---

## 2. Retry counts and timeouts  &nbsp;✅ ANSWERED (2026-06-17) — flat retry + 30-min job SLA

> **Client answer (2026-06-17):** **Flat retry, no backoff ladder.** One shared `retry` queue per network, single **60s TTL**, `max_retries=5` before `*.dead` (matches the R5 shared retry/dead topology). **Job-level SLA = 30 min default**, overridable per-`callingSystemId` via `calling_system_config` with `default` fallback — the same mechanism as every other SLA (A: §3.7, B: §5.7). The earlier escalating-TTL ladder, the 24h job SLA, and the separate 60-min manifest/chunk business waits are **dropped** — the single per-`callingSystemId` job SLA subsumes them (a job that never completes is forced terminal by the `TimeoutSweeper` at the SLA, regardless of which phase stalled).

| Setting | Value |
|---------|-------|
| NetworkA / NetworkB per-step retries | **5**, flat — shared `retry` queue, single **60s TTL**, then `*.dead` |
| Per-phase override | `phase_config.max_retries` (default 5) |
| Job-level SLA before forced timeout | **30 min default**, per-`callingSystemId` (`calling_system_config`, `default` fallback) |
| Separate manifest/chunk-arrival waits | **none** — subsumed by the job SLA |

> See also **§19** — the RabbitMQ `consumer_timeout` (worker-deadlock recovery) is a separate, lower-level broker timer, driven by max per-step processing time, not by these business-level values.

---

## 3. CSV semantics for partially-failed jobs  &nbsp;✅ ANSWERED — at-least-once accepted (consumer idempotent)

> **Client answer (2026-06-14):** At-least-once callback delivery is acceptable; the consuming system is treated as idempotent — a repeated callback for an already-processed `jobId`/`externalId` is safely ignored. **Applied:** no stronger delivery mechanism (outbox-routed dispatch) is required; `CompletedPartially` confirmed acceptable to downstream consumers.

When some files succeed and some fail/are unsupported:

- The CSV contains a row per file with the correct status. ✓ confirmed by spec.
- The `StatusCallbackPayload.JobStatus` is `CompletedPartially`. Confirm this is acceptable to downstream consumers.
- **Callback delivery is at-least-once (BLOCKERS N6).** On a crash between dispatch and the DB commit recording it, the same `StatusCallbackPayload` may be delivered more than once. **Confirm the consuming system is idempotent** — i.e. a repeated callback for an already-processed `jobId`/`externalId` is safely ignored. If the consumer cannot dedup, we must add a stronger delivery mechanism (e.g. outbox-routed dispatch) — flag now.

---

## 4. Should NetworkA persist the source request payload verbatim?  &nbsp;✅ ANSWERED — yes (JSONB on `job`)

> **Client answer (2026-06-14):** "Don't care." **Applied:** keep the proposed default — persist the source request payload verbatim as a JSONB column on `job` for audit / replay.

For audit / replay. We propose yes, stored as a JSONB column on `job`. Confirm acceptable.

---

## 5. Network B output for nested archives  &nbsp;✅ ANSWERED — render each archive container as a directory (last dot → underscore)

> **Client answer (2026-06-14):** For zip/rar/7z and any supported archive type, render each archive container `name.ext` as a directory named `name_ext` — replace the **last** dot with an underscore. The leaf file keeps its real extension. Example: `parent.zip/inner.zip/leaf.docx` → `target_path/parent_zip/inner_zip/leaf.docx`. **Applied:** this encoding is baked into `original_relative_path` at Network A.

The simplification says we no longer repack nested archives. The assembled files inside a nested archive will be placed under their *original relative path* (e.g., `parent.zip/inner.zip/leaf.docx` becomes `target_path/parent_zip/inner_zip/leaf.docx` — each archive container `name.ext` becomes a directory `name_ext`).

Confirm this is the desired output layout for the client, vs. some other flattening rule.

---

## 6. Idempotency on `ExternalId`  &nbsp;✅ ANSWERED (2026-06-17) — no idempotency; always start a new job

> **Client answer (2026-06-17):** `external_id` is **not guaranteed unique** and is **not** a dedup key — every submission **starts a new job**. **Applied:** the `UNIQUE` constraint on Postgres-A `job.external_id` is **dropped**; `external_id` is a pass-through correlation id (carried to the CSV/callback for the caller's own correlation). ARCH §6.2 updated.

Resubmitting the same `ExternalId` creates a **separate** `job` each time — the API does not return an existing `jobId`.

**Accepted consequence:** with no job-level dedup, at-least-once ingestion creates duplicate jobs — RabbitBridge redelivery (broker at-least-once), HTTP client retry, or a watcher re-picking a file each spawn a fresh job that runs end-to-end. If duplicates from a specific channel become a problem, that channel adds its own dedup (e.g. on the inbound message-id), **not** the `job` table.

---

## 7. Ingestion services: 4 separate or 1 with adapters?  &nbsp;✅ ANSWERED — 4 separate services

> **Client answer (2026-06-14):** 4 separate ingestion services for now. **Applied:** 4-service path confirmed (HTTP API, FolderWatcher, RabbitBridge, RequestFileWatcher remain independently deployed); no collapse to a single Ingestion service.

The current design has 4 ingestion services (HTTP API, FolderWatcher, RabbitBridge, RequestFileWatcher). Both independent reviewers proposed collapsing to **one Ingestion service** hosting all 4 channels as in-process HostedServices, sharing one image, one deployment, one DB pool.

- **Pro 4 services:** crash isolation per channel; channels deploy independently; one channel's bug can't block the others.
- **Pro 1 service:** one image, one CI pipeline, one set of dashboards, one HPA. Significantly less ops surface for the same throughput.

Both are defensible. The 4-service path is the current default per the user's earlier preference. Confirm this is still wanted or signal a switch to 1.

---

## 8. Average files per job (drives scale sizing)  &nbsp;⏳ STILL OPEN — no data; ~5 min/job target recorded

> **Client note (2026-06-14):** Client has **no data** on the average package / files-per-job. Client states the average **job** should take ~5 minutes, excluding proxy delay. **Status:** still open — these numbers drive partitioning + `consumer_timeout` sizing (§19); treat as a post-launch calibration item (measure from real traffic).

For partitioning, `chunk` table growth, and the "hot row on `job.files_manifest_written_count`" question: what is the **typical** number of leaf files per job, and the **p99**? Examples:

- "Most are single files; ~5% are archives with 10–50 files; one in a thousand is an archive with 1000+."
- "Most are folders with ~100 files; nothing larger than 500."

Answers materially affect retention sizing and whether counter-sharding is needed.

---

## 9. Storage retention SLA

How long must we retain:

1. **A's data outbox** files after the proxy has picked them up?
2. **B's work dir** (holding files + assembled buffer) after the job is `Done`?
3. **B's target_path** delivered files? (presumably client-owned forever, but confirm.)
4. **Postgres job/chunk tables**: how many days/months of historical jobs should remain queryable?

Without retention, 50 TB/month of disk fills up indefinitely.

---

## 10. Object storage availability  &nbsp;✅ ANSWERED — no S3; filesystem fallback

> **Client answer (2026-06-14):** S3 is **NOT** available. **Applied:** fall back to filesystem (RWX) for all internal `work/` directories on both A and B; we accept the operational cost. Couples with §15/§16 (RWX-everywhere assumed).

Is an S3-compatible object store available in the OpenShift cluster (ODF Noobaa, MinIO operator, external S3)? If yes, we will use it for internal `work/` directories on both A and B, which is more reliable than RWX PVs at this scale. If no, we fall back to RWX filesystem and accept the operational cost.

---

## 11. Converter cardinality  &nbsp;✅ ANSWERED — always 1→1

> **Client answer:** `IFileConverter` always takes one file and produces exactly one file (1→1). **Applied:** `converted_file` table collapsed into `source_file` (`applied_conversion` + `converted_relative_path`); `chunk` now keyed by `source_file_id`; Convert + Split CAS on `source_file`. Resolves N3 (chunk filename collision). See DB-SCHEMA, ARCHITECTURE §3.2/§3.4, MICROSERVICES.

When NetworkA's `IFileConverter` runs on a source file, does it ever produce **more than one** output file? (e.g. one source PDF → many extracted attachments, or one source archive → many decompressed files inside the conversion step.)

**Crisp question:** Can a single `IFileConverter` invocation emit ≥2 output files for one input file — yes or no?

**Impact by answer:**

| Answer | Schema / design impact |
|--------|------------------------|
| **Always 1 → 1** (or 1 → 0 pass-through) | Collapse `source_file` + `converted_file` into **one** table with `applied_conversion` + `converted_relative_path` columns. Removes a join and a CAS hop per file. |
| **1 → N is real** | Keep the **two-table** `source_file` → `converted_file` design as documented. `converted_file` carries its own `ON CONFLICT` key. No change. |

**Default if unanswered:** assume **1 → N real** — keep two tables. Safe (superset) but carries the extra table if the client never needs it.

**⚠ Correctness coupling (BLOCKERS N3), not just a table choice.** The chunk filename scheme is `{job_id}_{source_file_id}_chunk_{index}` — it has **no `converted_file_id`**. If 1 → N is real, two converted files of one source file both emit `_chunk_0`, the filenames collide, and `inbox.file_path` (PK) on Network B silently drops one → corrupt assembly. So a 1 → N answer **requires** changing the chunk filename to include `converted_file_id` and aggregating per-source-file completion across all converted files before writing the mini-manifest. A wrong assumption here corrupts data silently — treat with the same gravity as §12.

---

## 12. Converter determinism  &nbsp;✅ ASSUMED (2026-06-17) — all converters deterministic (pending client confirmation; see BLOCKERS S5)

> **Status (2026-06-17):** **Working assumption — every `IFileConverter` is deterministic** (same input bytes → same output bytes). This unblocks idempotent re-split + first-writer-wins dedup (BLOCKERS S5). It is an assumption pending client confirmation, not yet a verified fact. The one known carve-out remains: the Aspose `PDF → DOCX` converter is confirmed non-deterministic (rsid + timestamps) and **must be made deterministic via option (a) before it goes live**; option (b) (persist the first split output and re-serve exact bytes on retry) stays as the documented contingency. **Determinism sign-off is still required per converter before that converter ships.**

For redelivery safety, every `IFileConverter` must be **deterministic**: same input bytes → same output bytes. This matters because on a crash-retry, NetworkA re-runs the converter. If output differs, chunks already delivered to NetworkB (from the first attempt) are from a different version of the file than the chunks NetworkA re-sends — assembly is silently corrupt.

> **Confirmed non-deterministic (tested 2026-06-03):** The Aspose `PDF → DOCX` converter produces different byte output on each run for the same input PDF. SHA-256 hashes of two runs differed. Root cause: Aspose embeds `rsid` revision-session IDs and timestamps in the DOCX XML on every save. This converter **cannot be used with idempotent re-split** without mitigation.

**Note on `byte_length`:** Under the byte-stable-proxy assumption (§18, BLOCKERS S5) the proxy ships chunk bytes unchanged, so `byte_length` is **valid end-to-end and is retained** in the manifest protocol as **metadata/audit only**. **NetworkB does NOT validate received chunk size against it** — assembly is count-gated and concat proceeds regardless of any size difference. (This reverses the earlier "byte_length removed because the proxy changes size" decision.) With deterministic converters, a retry re-produces byte-identical chunks, so the failure mode is neither mixed-version assembly nor byte_length mismatch.

**Crisp question:** Does **every** converter in scope produce byte-identical output for byte-identical input? List any that use random seeds, current timestamp, embedded session/run IDs, hash-map iteration order, or thread-timing-dependent output.

**Impact by answer:**

| Answer | Design impact |
|--------|---------------|
| **All deterministic** | Idempotent re-split after crash is safe. Deterministic chunk names hold. |
| **Some non-deterministic (confirmed: Aspose PDF→DOCX)** | Option **(a)** make converter deterministic — fix Aspose output (strip rsid, inject fixed seed/timestamp, stable ordering). Or option **(b)** persist the first split output (chunks + manifest bytes) and re-serve those exact bytes on retry instead of re-running the converter. Option (b) adds a durable per-file artifact store on NetworkA. For Aspose specifically, option (a) may not be achievable without deep Aspose API control — treat as (b) until confirmed. |

**Retry strategy (under the determinism assumption):** On any retry, NetworkA re-sends **all** chunks for a file (never partial), and because the converter+splitter are deterministic, the re-sent chunks are **byte-identical**. NetworkB therefore uses **first-writer-wins** (`inbox.file_path` PK + `ON CONFLICT DO NOTHING`) — a duplicate delivery is safely ignored, not overwritten. (Supersedes the earlier "NB must overwrite (last-writer-wins)" rule, which was only needed when re-sent bytes could differ.)

**Default (current):** proceed under the deterministic-converter assumption (BLOCKERS S5), with per-converter determinism sign-off required before each converter goes live (Aspose PDF→DOCX is the known carve-out). Wrong assumption here causes silent data corruption, not just rework — so the sign-off gate is mandatory.

---

## 13. Per-file failure behavior  &nbsp;✅ ANSWERED — whole-file Failed on any chunk error

> **Client answer (2026-06-14):** Yes — mark the file as `Failed` in the CSV; any chunk error fails the whole file. **Applied:** whole-file failure on any chunk error (no partial recovery / no assemble-what's-there).

If a single chunk of a file arrives as `.ERROR.txt`, the whole file is marked `Failed` in the CSV (status=`FAILED`). Confirm this is desired vs. partial recovery (assemble what's there, mark only the missing range).

---

## 14. ~~Late-manifest behavior on NetworkB~~ — RESOLVED

This question is obsolete. Chunks are no longer NACK+requeue'd when their file-manifest has not yet arrived. Instead, chunks are durably **buffered** in `inbox` (`published=FALSE`) and drained inline when the file-manifest arrives. Similarly, file-manifests are buffered and drained when the job header arrives. No retry-count tuning is needed for this case.

---

## 15. Storage backend (local PV vs. object storage)  &nbsp;✅ ANSWERED (interim) — filesystem PV, RWX assumed

> **Client answer (2026-06-14):** Client first answered "RWO only" for the StorageClass, then clarified to assume (for now) that every pod can read AND write-many to every folder — i.e. treat all shared dirs as **RWX-capable**, no node-affinity pinning. **Applied:** combined with §10 (no S3), all `work/` volumes are filesystem-backed and RWX is assumed available. Keep local/shared filesystem PV; no S3 backend. Interim assumption — revisit if RWX-everywhere proves false.

Work dirs default to local PV. If the worker pod dies, the PV reattaches to its replacement pod automatically (OpenShift). The window of unavailability for a job is the pod restart time (~30s typical).

**Crisp question:** Is a ~30s per-job stall on pod restart acceptable, or must any worker resume any job immediately (→ object storage)?

**Impact by answer:**

| Answer | Design impact |
|--------|---------------|
| **~30s stall acceptable** | Keep local PV for `work/` dirs. No change. (Still needs RWX — see §16.) |
| **Immediate cross-worker resume required** | Set `IStorage` backend to **S3-compatible** object store. Config flag only, no code change. Requires §10 object store to exist. Removes the PV-reattach dependency entirely. |

**Default if unanswered:** local PV with RWX (per §16). Switch to S3 is a deploy-time flag, so this is reversible without code — but the §16 RWX availability question must be answered regardless.

---

## 16. RWX StorageClass availability  &nbsp;✅ ANSWERED (interim) — assume RWX everywhere

> **Client answer (2026-06-14):** Client answered "RWO only" for the StorageClass, then clarified to assume (for now) that every pod can read AND write-many to every folder — treat all shared dirs as RWX-capable, with **no node-affinity pinning**. **Applied:** proceed with the shared-PV (RWX) design as documented; no node-affinity rules, no object-storage-from-day-1 fallback. Interim assumption — if RWX truly is unavailable (RWO-only), revisit and adopt node-affinity pinning per the RWO row below.

**Context.** Work directories on both A and B default to a shared PV so multiple worker replicas (Prepare, Convert, Split on A; Assembly, ReverseConverter on B) can access the same job's files simultaneously. This requires an **RWX-capable StorageClass** in OpenShift (e.g., CephFS via ODF, NFS provisioner, Azure Files). If only RWO is available, each job must be pinned to the node that holds its PV via node affinity — limiting pod scheduling and making rolling restarts disruptive.

**Crisp question:** Does the target OpenShift cluster have an RWX-capable StorageClass? Name it (CephFS/NFS/Azure Files/etc.), or confirm RWO-only.

**Impact by answer:**

| Answer | Design impact (affects PV strategy from day 1) |
|--------|------------------------------------------------|
| **RWX available** | Shared PV as documented — all replicas of a phase access the same job dir concurrently. Current default. No change. |
| **RWO only** | Either (a) **node-affinity pinning** — each job pinned to the node holding its PV; limits scheduling, makes rolling restarts disruptive; or (b) **object storage from day 1** (§10/§15) so no shared filesystem is needed. Pick one. |

**Default if unanswered:** **cannot finalize PV/deployment manifests** — this changes node-affinity rules, HPA behavior, and rollout strategy from the first sprint. Must be answered before authoring Helm/PV templates.

---

## 17. `target_path` storage type and access pattern  &nbsp;✅ ANSWERED — network share (NFS/SMB), `.tmp` + rename

> **Client answer (2026-06-14):** `target_path` is a **network share (NFS/SMB)** for now. **Applied:** (1/4) writable **directly** from the Reporter/ReverseConverter pod — no relay service; (2) `.tmp` + rename is fine on this share; (3) the consuming system scans the directory but **ignores `.tmp` files**, so intermediate writes are safe; and if two jobs write to the same `target_path` and filenames collide, **overwrite (last-writer-wins)**.

**Context.** Network B's `ReverseConverter.Worker` writes assembled files into `target_path/<original_relative_path>` via `.tmp + rename` on the local filesystem. The architecture treats `target_path` as a client-owned filesystem interface. We need to know:

1. **Who provisions it?** Is it a PV mounted into the pod by us, a network share (NFS/SMB) mounted at the OS level, or a FUSE-mounted object-store bucket (e.g., s3fs)?
2. **Is `.tmp + rename` safe on that storage?** A cross-device rename (e.g., FUSE mount from a different filesystem) is not atomic. If `target_path` is a network share or object-store mount, we need a different delivery strategy (write directly, or stage on local PV and copy).
3. **Does the consuming system scan the directory?** If the consumer watches `target_path` for new files (similar to how the Proxy watches the outbox), it must not pick up `.tmp`-suffixed intermediate files. If the consumer only reads on explicit request, any write order is fine.
4. **Is `target_path` writable from the Reporter/ReverseConverter pod directly,** or does delivery go through a relay service?

**Question.** Provide the storage type, mount mechanism, and any consumer access-pattern constraints for `target_path` on Network B.

---

## 18. Proxy transform behavior  &nbsp;✅ RESOLVED under assumption (2026-06-17) — proxy is byte-stable (ships bytes unchanged)

> **Status (2026-06-17):** **Working assumption — the proxy does NOT transform chunk bytes** (ships each file unchanged, size-preserving), pending client confirmation (BLOCKERS S5). This matches ARCHITECTURE.md §4 ("ships each file unmodified"). Consequences: **§18.2, §18.3, §18.4 are MOOT** — there is no proxy transform for the reverse converter to tolerate, bound, or handle the failure mode of. ReverseConverter only undoes Network A's *forward* conversion per the mini-manifest's `reverse_conversion` (§5.5). `byte_length` is **reinstated** (see decisions below). §18.1 (delivery atomicity) was already RESOLVED — the proxy publishes its Rabbit message only after the file finishes moving (truncation guard).
>
> **Additional client side-notes (2026-06-14), folded into cleanup behavior:** NetworkB **moves/consumes** files out of the proxy folder — Assembly reads chunks in-place during concat, then best-effort deletes consumed chunks **post-COMMIT**; the proxy **self-cleans** days-old leftovers. A re-transferred file overwrites the same name on the proxy side, but NetworkB dedups **first-writer-wins** (`inbox.file_path` PK + `ON CONFLICT DO NOTHING`) — safe because deterministic re-split yields byte-identical chunks.

**Assumed behavior (2026-06-17, pending client confirmation — BLOCKERS S5):**
- Proxy is **byte-stable**: same input bytes → identical output bytes (no transform).
- Proxy is **size-preserving**: output size = input size.
- NetworkA therefore **knows** the exact `byte_length` to embed in the manifest, and it matches the bytes delivered to B.
- The reverse converters on NetworkB undo only Network A's **forward** conversion (per `reverse_conversion`), not any proxy transform.

> **Superseded premise (retained for traceability):** an earlier conversation note held the proxy transform to be non-deterministic and size-changing, which had forced `byte_length` out of the protocol and demanded last-writer-wins dedup. That premise is **reversed** by the byte-stable assumption above. If the client later confirms the proxy *does* transform bytes, revert this section and BLOCKERS S5, and reopen N9 / the dedup rule.

**Design decisions under the byte-stable assumption:**
- `byte_length` field **retained** in the chunk manifest protocol (`chunk.byte_length`, `expected_chunk.byte_length`, mini-manifest `byteLength`) as **metadata/audit only**. **NetworkB does NOT validate received chunk size against it** — assembly is count-gated and concat proceeds regardless of any size difference (the message-gated delivery, §1, is the truncation guard). Resolves N9 by keeping `byte_length` consistently.
- Retry strategy: NetworkA re-sends **all** chunks for a file on any retry; deterministic re-split makes them byte-identical, so NetworkB dedups **first-writer-wins** (`ON CONFLICT DO NOTHING`) — no overwrite.

**Open questions — status under the byte-stable assumption:**

1. **Proxy delivery atomicity (critical — links to §1).**  ✅ **RESOLVED (2026-06-14).** The proxy publishes its RabbitMQ message to NetworkB **only after** the file finishes moving into the delivery dir; NetworkB acts on the message and never observes a mid-write file. Truncation guard; `byte_length` (reinstated) is metadata only — **not** a validation check.

2. **Reverse converter variant tolerance.**  ✅ **MOOT** under the byte-stable assumption — no proxy transform exists, so there are no transform variants. ReverseConverter operates only on Network A's forward-converted output.

3. **Transform scope.**  ✅ **MOOT** — no proxy transform.

4. **Transform failure mode.**  ✅ **MOOT** — the proxy does not transform, so there is no transform-failure mode. (Proxy *delivery* failure is still signalled by the existing `.ERROR.txt` / `.UNSUPPORTED.txt` sentinels, handled by ProxyListener branches ⑥/⑦.)

> If the client later confirms the proxy DOES transform bytes, questions 2–4 reopen and the reverse-converter design must handle transform variants/scope/failure.

**Impact if unanswered:**

| Question | Status under byte-stable assumption |
|----------|----------------|
| §18.1 (atomic delivery) | ✅ Resolved — proxy publishes Rabbit message only after the move; NB never observes a mid-write file |
| §18.2 (reverse converter tolerance) | ✅ Moot — no proxy transform; reopens only if the assumption is reversed |
| §18.3 (transform scope) | ✅ Moot — no proxy transform |
| §18.4 (transform failure mode) | ✅ Moot — no proxy transform (delivery-failure sentinels unchanged) |

---

## 19. Worker-recovery calibration (RabbitMQ `consumer_timeout`)  &nbsp;⏳ TUNE FROM METRICS (not a pre-dev blocker)

**Context.** Recovery from a crashed/hung worker is handled by RabbitMQ redelivery, not by a bespoke heartbeat/sweeper (see ARCHITECTURE.md §3.3, BLOCKERS S1). Recovery latency by failure mode:

- **Process crash / OOM / pod restart** → the broker requeues the unacked message on connection close → **seconds**. (No tuning needed.)
- **Network partition / hard kill** → AMQP connection heartbeat detects it → **~60s**. (No tuning needed.)
- **App deadlock / infinite loop (connection still alive)** → the broker waits `consumer_timeout`, then closes the channel and requeues → **up to `consumer_timeout`** (RabbitMQ default **30 min**). This is the only knob.

`consumer_timeout` must be set **above the longest legitimate single-message processing time** of any phase — otherwise a healthy-but-slow step is killed mid-work, requeued, and (after the `delivery-limit`, 20 — the 4.x default, pinned via policy) false-dead-lettered. So it is bounded below by max step time and above by acceptable deadlock-recovery latency.

**Crisp questions:**
1. **Max single-file processing time per phase** — the slowest a single source file can take to Prepare/Convert/Split (A) and Assemble/Reverse-convert (B). A rough p99 + worst-case ceiling is enough. (We can also measure this from the per-step duration histogram after ~1 week of real traffic — see §8 files-per-job, which drives file sizes.)
2. **Acceptable recovery latency for a deadlocked worker** — is "up to ~`consumer_timeout`" (minutes) acceptable for a stuck file, given the job-level SLA already backstops the *job* at 30 min? Or must a stuck file be reclaimed faster?

**Impact by answer:**

| Answer | Design impact |
|--------|---------------|
| Max step time known + minutes-level recovery acceptable | Set `consumer_timeout` ≈ 3–4× max step time (start at 30-min default, tune down). No code change — broker config. |
| Max step time **> job SLA** (a single file can take longer than the 30-min §2 SLA) | The **SLA is mis-set**, independent of recovery design — must raise the SLA or split the work. Surface before launch. |
| A specific long phase needs **faster-than-`consumer_timeout`** reclaim | Re-introduce a per-row heartbeat + single-replica sweeper **for that one phase** via `phase_config` (the escape hatch). Localized, not global. |

**Default if unanswered:** ship with `consumer_timeout=30min` (RabbitMQ default), `prefetch=1`, quorum `delivery-limit=20` (4.x default; pinned explicitly via policy), **plus the client-side watchdog** (see §20) which reclaims a hung worker in seconds; calibrate from the per-step duration histogram post-launch. Safe — the job-level `TimeoutSweeper` (B, §2 SLA) and `TimeoutSweeper-A` (ARCHITECTURE.md §3.7) backstop any worker that never recovers.

---

## 20. RabbitMQ version & queue type  &nbsp;✅ ANSWERED — RabbitMQ 4.3, quorum queues  ·  ⏳ topology sub-item open (3-node vs single-node)

> **Client answer (2026-06-17, revised):** RabbitMQ **4.3**, **quorum queues**. *(Supersedes the earlier 3.13 pin — the client chose to upgrade to 4.x specifically to get native quorum **message priority** for per-`callingSystemId` job priority, see §21.)* **Applied:** quorum is the only queue type; all classic-queue content removed. PostgreSQL stays at **16**. All backstops the recovery design uses are available on 4.x quorum queues (`consumer_timeout`, `delivery-limit`, `dead-letter-strategy=at-least-once`) **plus native message priority**.

**RabbitMQ 4.3 quorum specifics:**

- **`consumer_timeout`** — enforced on quorum queues; default **30 min**, evaluated at 1-minute intervals. The client-side watchdog (ARCHITECTURE.md §3.3) reclaims a hung worker faster and is the primary defense; `consumer_timeout` is the coarse broker backstop.
- **`delivery-limit`** — **defaults to 20 on 4.x**. We keep **20** and still set it **explicitly via policy** (clarity + pinned across upgrades). It is the broker poison backstop, parallel to the app-level `max_retries=5`.
- **`dead-letter-strategy=at-least-once`** — on 4.x **requires** the queue also use `overflow=reject-publish` (it does **not** work with the default `drop-head`) **and** a configured dead-letter-exchange. Both are already set by this topology, so DLX re-publishes use publisher confirms internally → no silent message loss.
- **Message priority** — quorum queues support it natively on 4.x with **32 strict priority levels (0–31)**, always enabled (`x-max-priority` is ignored — it applies only to classic queues; classic queues go up to 255). A message with no `priority` property defaults to **4**. Priority is **strict** (higher always dispatched ahead of lower — no fairness), so sustained high-priority traffic can starve lower levels. This is what powers per-`callingSystemId` priority (§21) with **no priority-lane topology**.
- **Classic queue mirroring** — removed in 4.0. Not used.

**Remaining open sub-item — cluster size (recommend 3-node):**

Quorum queues only deliver their data-safety/HA guarantee on a **3-node (odd-sized) cluster** — each queue is replicated across all 3 nodes (Raft majority), so one node can be lost with **zero message loss and zero downtime** (leader re-elected elsewhere). On a **single node**, quorum still works and is durable to local disk on clean restart, but: (a) a node outage stalls the whole pipeline until it returns, and (b) a disk failure loses in-flight (published-but-unconsumed) messages — the transactional outbox cannot resend them because `published_at` is already set, so those files stall until the 30-min `TimeoutSweeper`. A single node also pays quorum's Raft fsync cost for **zero** replication benefit.

| Answer | Design impact |
|--------|---------------|
| **3-node cluster per network** (recommended) | Quorum as intended — replicated, survives one node loss. Matches guiding principle #3 (crash-safe). Documented default. |
| **Single node per network** | Functional but a broker **SPOF**: pipeline stalls on node outage; in-flight messages lost on disk failure (recovered only at the 30-min SLA). Accept explicitly if cost-driven. |

**Default if unanswered:** **3-node cluster per network.** Reversible — cluster size is a deployment/topology change (`definitions.json` + StatefulSet replica count), no application code change.

---

## 21. Per-`callingSystemId` configuration, reverse conversion & priority  &nbsp;✅ ANSWERED (2026-06-17) — config model + priority both set

> **Client answer (2026-06-17):** All caller-varying configuration is keyed by `callingSystemId` with a `default` fallback. The proxy/processing config lives on **Network A** and the **reverse-conversion instruction is carried to Network B via the mini-manifest** (B holds no conversion config). SLA/timeout is an **independent table per network**. **Priority: ENABLED** — the client chose to upgrade to **RabbitMQ 4.3** (§20) so per-`callingSystemId` job priority uses the broker's native quorum message priority.

**What is per-`callingSystemId` (with `default` fallback):**

- **Proxy/processing config (Network A, `conversion_rule`, keyed `(callingSystemId, source_format)`).** Example rule the client gave:
  ```jsonc
  {
    "SourceFormat":       "heic",   // matches source_file.original_format
    "RequiredConversion": "PNG",    // forward conversion on A; null = pass-through
    "reverseConversion":  "heic",   // reverse target on B; null = no reverse; may differ from SourceFormat
    "FileSizeLimitMb":    200       // Split.Worker max bytes per chunk (split sizing — NOT an ingestion reject limit)
  }
  ```
  Resolution precedence (most specific wins): `(callingSystemId, sourceFormat)` → `(callingSystemId, 'default')` → `('default', sourceFormat)` → `('default', 'default')`. Convert.Worker snapshots `applied_conversion` / `reverse_conversion` / `file_size_limit_mb` onto `source_file`, freezing the rule per job.
- **Folders + SLA (both networks, `calling_system_config`).** Per-caller outbox/work folder locations and the job-level SLA/timeout. **SLA is independent per network** — A has its own (§3.7), B has its own (§5.7); the operator sets both and keeps them aligned per `callingSystemId`. Broker-level timers (`consumer_timeout`, AMQP heartbeat, `prefetch`) stay global — they are queue/node-scoped and cannot be per-caller.

**Reverse conversion semantics (clarified):** reverse conversion is **not** the inverse of the forward conversion. It is an independent per-`(callingSystemId, source_format)` target: `null` → file type needs **no** reverse conversion (pass-through on B); equal to the source format → undo the forward conversion; a different value → reverse-convert to a **different** type. A resolves it; the mini-manifest's `reverseConversion` field carries it to B verbatim; ReverseConverter obeys it (§5.5).

**Clarification captured:** `FileSizeLimitMb` is a **splitting-logic parameter** (max chunk size for Split.Worker), not an ingestion/package size cap — no request is rejected on size.

**Job priority by `callingSystemId` (enabled on RabbitMQ 4.3):**

The client opted to upgrade to **RabbitMQ 4.3** (§20), whose quorum queues support **native message priority** — so priority needs no topology workaround (no separate high/normal lanes, no dedicated worker pools). Mechanism:

- `calling_system_config.priority` (both networks, `default` fallback) holds the per-caller priority.
- **Network A** snapshots it onto `job.priority` at ingestion; **Network B** resolves it from **its own** `calling_system_config` when it creates the job row from the header. Priority is **independent per network** (same model as SLA); it is *not* carried in the job header — the operator keeps the two sides aligned per `callingSystemId`.
- Every next-step message copies `job.priority` into `outbox.priority`; **OutboxRelay** sets the AMQP `priority` property from it on publish (publisher-confirmed). Quorum honors it natively (0–31, higher first); the property survives the `retry`/`reroute` round-trip.
- Applies **end-to-end** across all phases because every queue is quorum and every publish goes through OutboxRelay.

**32 graded levels (0–31), default 4:** 4.x quorum priority supports a real graded scale — callers can be ranked finely, not just "VIP vs normal." Set `calling_system_config.priority` per caller (baseline `4`, higher = more urgent). `x-max-priority` is not set (ignored by quorum; priority is always on).

**Strict priority — starvation is the constraint to communicate:** quorum priority is *strict* (higher always dispatched before lower, no fairness). A caller that submits high-priority work continuously **can starve** lower-priority jobs across the shared queues. Mitigations: keep the priority spread tight, reserve the top of the range for genuinely urgent callers, and alert on per-priority queue depth. The 30-min job SLA (§2) still backstops any starved job into a `TimedOut` rather than hanging forever.

**Status:** config model **and** priority **applied to docs**. Open item: cluster size (§20, 3-node recommended).

---

## 22. Large-file (video) handling & per-engine SLA  &nbsp;⏳ OPEN — surfaced by the File Conversion Table

> **Context (2026-06-25):** The client's File Conversion Table groups all conversions into **3 engines** — ImageMagick (images→PNG, 200 MB), Aspose (pdf/ppt/pptx→DOCX, 100–200 MB), and **ffmpeg (all audio+video→MP4/H264/AAC, `FileSizeLimitMb=6000`)**. The converter is split by engine accordingly (one codebase deployed per group; ARCHITECTURE §3.1/§5.1/§10 #23). The video group's **6 GB chunk-size limit** is an order of magnitude larger than every other group and drives this question.

**Engine grouping (decided, recorded for confirmation).** Convert deploys 3× on A (`files.convert.image|doc|video`) and Reverse deploys 2× on B (`files.reverse.image|doc`; no video — every A/V row's Target Format is "No conversion needed", so it delivers as-is). Pass-through formats (docx, doc, xls, xlsx, xml, rtf, srt, json, txt, kml) and the rename-to-TXT group (obj, mtl, tfw, csv, upscsv) carry `converter_group=NULL` and skip the converter entirely. **Confirm this grouping matches operational expectations** (e.g. Aspose licensing scope, ffmpeg node pool).

**Crisp questions:**
1. **Video SLA.** A multi-GB ffmpeg transcode + a 6 GB split + cross-proxy transit + B-side concat can far exceed the 30-min default job SLA (§2). What SLA should the `video` group's callers get? (Per-`callingSystemId` SLA already supports a different value — `calling_system_config.sla_minutes`.)
2. **Storage sizing for 6 GB chunks.** Are A's work/outbox dirs, the proxy delivery dir, and B's assembled work dir provisioned for multi-GB files at the expected video concurrency? (Couples with §9 retention and §15/§16 RWX.)
3. **`consumer_timeout` per group.** Confirm the `video` queue gets a much larger `consumer_timeout` + watchdog bound than image/doc (calibrated from real transcode times; ARCHITECTURE §3.3).
4. **Rename-to-TXT transit.** obj/mtl/tfw/csv/upscsv are relabeled to `.txt` for transit then delivered at their original extension (the table's Target Format). Confirm this relabel is only to satisfy a proxy extension allow-list and the bytes are unchanged (no engine).

**Impact if unanswered:** ship with one 30-min SLA + uniform pod sizing; video jobs that exceed it get forced to `TimedOut` by the sweeper (no data loss, but a false timeout). Per-group SLA/resources are deploy-time config — reversible without code.
