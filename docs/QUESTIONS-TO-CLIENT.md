# Open Questions for the Client

These items need a client/stakeholder decision before final implementation.

---

## 1. Direct writes to the Proxy outbox (no `.tmp` + rename)

**Context.** NetworkA writes chunks and the manifest JSON directly into the proxy outbox directories. We were told the proxy may pick up `.tmp` (or any temp-named) files, so the standard atomic-write trick (write to `.tmp`, then rename to the final name) cannot be used.

**Risk.** The proxy watches the outbox via the filesystem. If it sees the file before NetworkA finishes writing, it could ship a truncated file. With the default `FileSystemWatcher`-style behaviors on Windows/Linux, file-creation events fire on open, not on close, which means partial reads are possible.

**Mitigation options to choose from:**

- **A. Accept the risk.** Empirically the proxy is slow enough / debounces enough that this never bites in practice. *Recommended only if proxy operator confirms a debounce or post-stable check.*
- **B. Write to a staging sub-directory that the proxy does not watch, then `mv` (single-rename, no `.tmp` suffix) into the watched dir.** A `mv` within the same filesystem is atomic. The destination filename is the final name from first appearance.
- **C. Write to a separate filesystem and `mv` across.** Not atomic — same problem as direct writes.
- **D. Ask the proxy operator to ignore files until a sibling `<name>.ready` marker appears.** Requires proxy change.
- **E. Ship via the manifest path: write a per-file marker into the manifest outbox dir only after the chunk file is fully closed.** Requires proxy logic change.

**Question.** Which mitigation is acceptable, or does the proxy already debounce on inactivity? Option **B** is cheapest on our side if confirmed compatible.

**Related — proxy *delivery* atomicity on the B side (BLOCKERS N7).** ⚠ **Elevated severity.** The proxy transforms chunks in transit non-deterministically and changes their size (see §18). This means NetworkB can no longer use `byte_length`-vs-observed-size validation to detect truncation — that signal is dead. **Proxy atomic write to B's delivery dir is now the only truncation-detection mechanism.** Does the proxy write each delivered file atomically (e.g. temp + rename), or can NetworkB observe a file mid-write? If non-atomic, there is no safe way to detect a truncated chunk delivery. The 200ms two-stat check is also insufficient since byte_length is gone. Must confirm: proxy writes to NetworkB delivery dir are atomic (complete-or-nothing), or we accept silent truncation risk.

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

---

## 3. CSV semantics for partially-failed jobs

When some files succeed and some fail/are unsupported:

- The CSV contains a row per file with the correct status. ✓ confirmed by spec.
- The `StatusCallbackPayload.JobStatus` is `CompletedPartially`. Confirm this is acceptable to downstream consumers.
- **Callback delivery is at-least-once (BLOCKERS N6).** On a crash between dispatch and the DB commit recording it, the same `StatusCallbackPayload` may be delivered more than once. **Confirm the consuming system is idempotent** — i.e. a repeated callback for an already-processed `jobId`/`externalId` is safely ignored. If the consumer cannot dedup, we must add a stronger delivery mechanism (e.g. outbox-routed dispatch) — flag now.

---

## 4. Should NetworkA persist the source request payload verbatim?

For audit / replay. We propose yes, stored as a JSONB column on `job`. Confirm acceptable.

---

## 5. Network B output for nested archives

The simplification says we no longer repack nested archives. The assembled files inside a nested archive will be placed under their *original relative path* (e.g., `parent.zip/inner.zip/leaf.docx` becomes `target_path/parent/inner/leaf.docx`).

Confirm this is the desired output layout for the client, vs. some other flattening rule.

---

## 6. Idempotency on `ExternalId`

If the same `ExternalId` is submitted twice, the API returns the existing `jobId` instead of creating a duplicate. Confirm this is the desired behavior (vs. forcing a new job each time). The response in this case will also include the current job status, so the caller can distinguish "still running" from "already finished."

---

## 7. Ingestion services: 4 separate or 1 with adapters?

The current design has 4 ingestion services (HTTP API, FolderWatcher, RabbitBridge, RequestFileWatcher). Both independent reviewers proposed collapsing to **one Ingestion service** hosting all 4 channels as in-process HostedServices, sharing one image, one deployment, one DB pool.

- **Pro 4 services:** crash isolation per channel; channels deploy independently; one channel's bug can't block the others.
- **Pro 1 service:** one image, one CI pipeline, one set of dashboards, one HPA. Significantly less ops surface for the same throughput.

Both are defensible. The 4-service path is the current default per the user's earlier preference. Confirm this is still wanted or signal a switch to 1.

---

## 8. Average files per job (drives scale sizing)

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

## 10. Object storage availability

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

## 12. Converter determinism  &nbsp;⛔ PRE-DEV BLOCKER (B10) — most critical

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

## 13. Per-file failure behavior

If a single chunk of a file arrives as `.ERROR.txt`, the whole file is marked `Failed` in the CSV (status=`FAILED`). Confirm this is desired vs. partial recovery (assemble what's there, mark only the missing range).

---

## 14. ~~Late-manifest behavior on NetworkB~~ — RESOLVED

This question is obsolete. Chunks are no longer NACK+requeue'd when their file-manifest has not yet arrived. Instead, chunks are durably **buffered** in `inbox` (`published=FALSE`) and drained inline when the file-manifest arrives. Similarly, file-manifests are buffered and drained when the job header arrives. No retry-count tuning is needed for this case.

---

## 15. Storage backend (local PV vs. object storage)  &nbsp;⛔ PRE-DEV BLOCKER (B10, with §16)

Work dirs default to local PV. If the worker pod dies, the PV reattaches to its replacement pod automatically (OpenShift). The window of unavailability for a job is the pod restart time (~30s typical).

**Crisp question:** Is a ~30s per-job stall on pod restart acceptable, or must any worker resume any job immediately (→ object storage)?

**Impact by answer:**

| Answer | Design impact |
|--------|---------------|
| **~30s stall acceptable** | Keep local PV for `work/` dirs. No change. (Still needs RWX — see §16.) |
| **Immediate cross-worker resume required** | Set `IStorage` backend to **S3-compatible** object store. Config flag only, no code change. Requires §10 object store to exist. Removes the PV-reattach dependency entirely. |

**Default if unanswered:** local PV with RWX (per §16). Switch to S3 is a deploy-time flag, so this is reversible without code — but the §16 RWX availability question must be answered regardless.

---

## 16. RWX StorageClass availability  &nbsp;⛔ PRE-DEV BLOCKER (B10, with §15)

**Context.** Work directories on both A and B default to a shared PV so multiple worker replicas (Prepare, Convert, Split on A; Assembly, ReverseConverter on B) can access the same job's files simultaneously. This requires an **RWX-capable StorageClass** in OpenShift (e.g., CephFS via ODF, NFS provisioner, Azure Files). If only RWO is available, each job must be pinned to the node that holds its PV via node affinity — limiting pod scheduling and making rolling restarts disruptive.

**Crisp question:** Does the target OpenShift cluster have an RWX-capable StorageClass? Name it (CephFS/NFS/Azure Files/etc.), or confirm RWO-only.

**Impact by answer:**

| Answer | Design impact (affects PV strategy from day 1) |
|--------|------------------------------------------------|
| **RWX available** | Shared PV as documented — all replicas of a phase access the same job dir concurrently. Current default. No change. |
| **RWO only** | Either (a) **node-affinity pinning** — each job pinned to the node holding its PV; limits scheduling, makes rolling restarts disruptive; or (b) **object storage from day 1** (§10/§15) so no shared filesystem is needed. Pick one. |

**Default if unanswered:** **cannot finalize PV/deployment manifests** — this changes node-affinity rules, HPA behavior, and rollout strategy from the first sprint. Must be answered before authoring Helm/PV templates.

---

## 17. `target_path` storage type and access pattern

**Context.** Network B's `ReverseConverter.Worker` writes assembled files into `target_path/<original_relative_path>` via `.tmp + rename` on the local filesystem. The architecture treats `target_path` as a client-owned filesystem interface. We need to know:

1. **Who provisions it?** Is it a PV mounted into the pod by us, a network share (NFS/SMB) mounted at the OS level, or a FUSE-mounted object-store bucket (e.g., s3fs)?
2. **Is `.tmp + rename` safe on that storage?** A cross-device rename (e.g., FUSE mount from a different filesystem) is not atomic. If `target_path` is a network share or object-store mount, we need a different delivery strategy (write directly, or stage on local PV and copy).
3. **Does the consuming system scan the directory?** If the consumer watches `target_path` for new files (similar to how the Proxy watches the outbox), it must not pick up `.tmp`-suffixed intermediate files. If the consumer only reads on explicit request, any write order is fine.
4. **Is `target_path` writable from the Reporter/ReverseConverter pod directly,** or does delivery go through a relay service?

**Question.** Provide the storage type, mount mechanism, and any consumer access-pattern constraints for `target_path` on Network B.

---

## 18. Proxy transform behavior  &nbsp;⛔ PRE-DEV BLOCKER — affects protocol and retry design

**Known facts (confirmed in conversation):**
- Proxy transform is **non-deterministic**: same input bytes → different output bytes across runs.
- Proxy **changes file size**: output size ≠ input size, and is not predictable by NetworkA.
- NetworkA **cannot** know the post-transform `byte_length` to embed in the manifest.
- NetworkB hosts reverse converters that may need to undo the proxy transform before assembly.

**Design decisions already taken based on the above:**
- `byte_length` field **removed** from the chunk manifest protocol. NetworkB no longer validates received size against manifest. Truncation detection relies entirely on proxy atomic delivery (§1).
- Retry strategy: NetworkA re-sends **all** chunks for a file on any retry. NetworkB **overwrites** existing inbox entries on duplicate chunk filename (last-writer-wins). This prevents mixed-version chunks from coexisting across retry runs.

**Open questions requiring client/operator answers:**

1. **Proxy delivery atomicity (critical — links to §1).** Does the proxy write each chunk to NetworkB's delivery dir atomically (e.g. temp + rename, or equivalent)? With `byte_length` gone, this is the **only** mechanism preventing NetworkB from assembling a truncated chunk. If non-atomic, there is no safe truncation guard and we accept silent corruption risk.

2. **Reverse converter variant tolerance.** The proxy transform is non-deterministic. Does the reverse converter in NetworkB need to handle multiple possible transform variants of the same source content, or does it operate on content semantics only (i.e., it doesn't care about how the proxy encoded/wrapped the bytes)?

3. **Transform scope.** Does the proxy transform apply to every chunk uniformly, or only to certain file types / size ranges? Knowing the scope helps bound the reverse converter's input surface.

4. **Transform failure mode.** If the proxy fails to transform a chunk (e.g. unsupported format), does it: (a) drop the file, (b) pass it through unchanged, or (c) write an error marker? NetworkB needs to handle whichever case applies.

**Impact if unanswered:**

| Question | Risk if skipped |
|----------|----------------|
| §18.1 (atomic delivery) | Silent truncated-chunk assembly — no detection, no error |
| §18.2 (reverse converter tolerance) | Reverse converter fails or corrupts on unexpected transform variant |
| §18.3 (transform scope) | Cannot bound reverse converter input surface; may miss edge cases |
| §18.4 (transform failure mode) | Unexpected proxy error format causes unhandled exception or silent drop on NetworkB |
