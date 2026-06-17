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

**Related — proxy *delivery* atomicity on the B side (BLOCKERS N7).**  ✅ **RESOLVED (2026-06-14).** The proxy publishes its RabbitMQ message to NetworkB **only after** the file finishes moving into B's delivery dir. NetworkB acts on the message, never scans the directory mid-write, and so can never observe a truncated file — this is the truncation guard that replaces the now-dead `byte_length`-vs-observed-size check. (Original concern retained below for context.) The proxy transforms chunks in transit non-deterministically and changes their size (see §18). This means NetworkB can no longer use `byte_length`-vs-observed-size validation to detect truncation — that signal is dead. **Proxy publish-after-move (message-gated delivery) is now the truncation-detection mechanism.** Does the proxy write each delivered file atomically (e.g. temp + rename), or can NetworkB observe a file mid-write? *(Answered: NB is gated on the post-move Rabbit message, so it never observes a mid-write file.)* The 200ms two-stat check is also insufficient since byte_length is gone.

---

## 2. Retry counts and timeouts

We've assumed defaults below. Confirm or override per phase:

| Setting | Default proposed |
|---------|------------------|
| NetworkA per-step retry attempts | 5 (TTLs 30s, 2m, 10m, 1h, 6h) |
| NetworkB per-step retry attempts | 5 (same ladder) |
| Manifest-arrival wait on NetworkB | 60 minutes |
| Chunks-arrival wait after manifest on NetworkB | 60 minutes |
| Job-level total SLA before forced timeout | 24 hours |

> See also **§19** — the RabbitMQ `consumer_timeout` (worker-deadlock recovery) is a separate, lower-level timer than these business-level waits, and is driven by max per-step processing time.

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

## 6. Idempotency on `ExternalId`

If the same `ExternalId` is submitted twice, the API returns the existing `jobId` instead of creating a duplicate. Confirm this is the desired behavior (vs. forcing a new job each time). The response in this case will also include the current job status, so the caller can distinguish "still running" from "already finished."

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

## 12. Converter determinism  &nbsp;⛔ PRE-DEV BLOCKER (B10) — owned + in progress

> **Status (2026-06-14):** The implementer who owns the transformation + split logic will fix the Aspose `PDF → DOCX` non-determinism via **option (a)** — make the converter deterministic. **Option (b)** (persist the first split output and re-serve the exact bytes on retry) is **kept as the documented contingency** if (a) proves infeasible. Still a pre-dev item, now owned and in progress.

For redelivery safety, every `IFileConverter` must be **deterministic**: same input bytes → same output bytes. This matters because on a crash-retry, NetworkA re-runs the converter. If output differs, chunks already delivered to NetworkB (from the first attempt) are from a different version of the file than the chunks NetworkA re-sends — assembly is silently corrupt.

> **Confirmed non-deterministic (tested 2026-06-03):** The Aspose `PDF → DOCX` converter produces different byte output on each run for the same input PDF. SHA-256 hashes of two runs differed. Root cause: Aspose embeds `rsid` revision-session IDs and timestamps in the DOCX XML on every save. This converter **cannot be used with idempotent re-split** without mitigation.

**Note on `byte_length`:** The original concern about `byte_length` mismatch after retry is now superseded. The proxy transforms chunks non-deterministically and changes their size (§18), so `byte_length` is removed from the manifest protocol entirely. The determinism concern remains — but the failure mode is now mixed-version chunk assembly rather than byte_length mismatch.

**Crisp question:** Does **every** converter in scope produce byte-identical output for byte-identical input? List any that use random seeds, current timestamp, embedded session/run IDs, hash-map iteration order, or thread-timing-dependent output.

**Impact by answer:**

| Answer | Design impact |
|--------|---------------|
| **All deterministic** | Idempotent re-split after crash is safe. Deterministic chunk names hold. |
| **Some non-deterministic (confirmed: Aspose PDF→DOCX)** | Option **(a)** make converter deterministic — fix Aspose output (strip rsid, inject fixed seed/timestamp, stable ordering). Or option **(b)** persist the first split output (chunks + manifest bytes) and re-serve those exact bytes on retry instead of re-running the converter. Option (b) adds a durable per-file artifact store on NetworkA. For Aspose specifically, option (a) may not be achievable without deep Aspose API control — treat as (b) until confirmed. |

**Retry strategy required regardless:** On any retry, NetworkA must re-send **all** chunks for a file (never partial). NetworkB must **overwrite** existing inbox chunks on duplicate filename (not skip/ignore). This prevents mixed-version chunks from different retry runs coexisting in NetworkB's inbox.

**Default if unanswered:** cannot safely proceed for any non-listed converter — treat determinism as unverified and require sign-off per converter before that converter goes live. Wrong assumption here causes silent data corruption, not just rework.

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

## 18. Proxy transform behavior  &nbsp;⚠ PARTIAL — §18.1 ✅ ANSWERED; §18.2–§18.4 still open

> **Status (2026-06-14):** §18.1 (delivery atomicity) is **RESOLVED** — the proxy publishes its RabbitMQ message to NetworkB only *after* the file finishes moving, so NB never observes a mid-write file (no truncation). See annotation on §18.1 below and the resolved note in §1. **§18.2, §18.3, §18.4** (reverse-converter transform tolerance, transform scope, transform failure mode) **remain the open parts of §18.**
>
> **Additional client side-notes (2026-06-14), folded into cleanup behavior:** NetworkB **moves/consumes** files out of the proxy folder — Assembly reads chunks in-place during concat, then best-effort deletes consumed chunks **post-COMMIT**; the proxy **self-cleans** days-old leftovers. The proxy **overwrites** files re-transferred to NetworkB with the same name + extension (supports first-writer-wins dedup under determinism).

**Known facts (confirmed in conversation):**
- Proxy transform is **non-deterministic**: same input bytes → different output bytes across runs.
- Proxy **changes file size**: output size ≠ input size, and is not predictable by NetworkA.
- NetworkA **cannot** know the post-transform `byte_length` to embed in the manifest.
- NetworkB hosts reverse converters that may need to undo the proxy transform before assembly.

**Design decisions already taken based on the above:**
- `byte_length` field **removed** from the chunk manifest protocol. NetworkB no longer validates received size against manifest. Truncation detection relies entirely on proxy atomic delivery (§1).
- Retry strategy: NetworkA re-sends **all** chunks for a file on any retry. NetworkB **overwrites** existing inbox entries on duplicate chunk filename (last-writer-wins). This prevents mixed-version chunks from coexisting across retry runs.

**Open questions requiring client/operator answers:**

1. **Proxy delivery atomicity (critical — links to §1).**  ✅ **RESOLVED (2026-06-14).** The proxy publishes its RabbitMQ message to NetworkB **only after** the file finishes moving into the delivery dir; NetworkB acts on the message and never observes a mid-write file. This message-gated delivery is the truncation guard that replaces the dead `byte_length` check. *(Original question retained:)* Does the proxy write each chunk to NetworkB's delivery dir atomically (e.g. temp + rename, or equivalent)? With `byte_length` gone, this is the **only** mechanism preventing NetworkB from assembling a truncated chunk. If non-atomic, there is no safe truncation guard and we accept silent corruption risk.

2. **Reverse converter variant tolerance.** The proxy transform is non-deterministic. Does the reverse converter in NetworkB need to handle multiple possible transform variants of the same source content, or does it operate on content semantics only (i.e., it doesn't care about how the proxy encoded/wrapped the bytes)?

3. **Transform scope.** Does the proxy transform apply to every chunk uniformly, or only to certain file types / size ranges? Knowing the scope helps bound the reverse converter's input surface.

4. **Transform failure mode.** If the proxy fails to transform a chunk (e.g. unsupported format), does it: (a) drop the file, (b) pass it through unchanged, or (c) write an error marker? NetworkB needs to handle whichever case applies.

**Impact if unanswered:**

| Question | Risk if skipped |
|----------|----------------|
| §18.1 (atomic delivery) | ✅ Resolved — proxy publishes Rabbit message only after the move; NB never observes a mid-write file |
| §18.2 (reverse converter tolerance) | Reverse converter fails or corrupts on unexpected transform variant |
| §18.3 (transform scope) | Cannot bound reverse converter input surface; may miss edge cases |
| §18.4 (transform failure mode) | Unexpected proxy error format causes unhandled exception or silent drop on NetworkB |

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
