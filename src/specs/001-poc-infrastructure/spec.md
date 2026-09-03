# Feature Specification: POC Infrastructure Setup

**Feature Branch**: `001-poc-infrastructure`  
**Created**: 2026-03-08  
**Status**: Draft  
**Input**: User description: "Create a POC for infrastructure setup including all interfaces, models, database connection, Temporal connection, and RabbitMQ, with mocks for complex logic to run the complete application flow end-to-end."

## User Scenarios *(mandatory)*

### User Story 1 - End-to-End Happy Path Flow (Priority: P1)

A developer triggers a file transfer job through the Ingestion API and observes the complete flow execute from ingestion in Network A, through the simulated Proxy, to assembly and callback in Network B — all without implementing actual file transformation logic.

**Why this priority**: This is the core proof-of-concept goal. If the entire infrastructure is wired correctly and the flow completes from A to B, the POC is successful.

**Independent Test**: Start all services and submit a valid Ingestion Request Payload to the API. Observe via Temporal UI and logs that the Decomposition Workflow starts, activities execute in order, the manifest and chunks are written to outbox directories, the Assembly Workflow starts in Network B, all parts are aggregated, and a Status Callback Payload with `COMPLETED` is returned to Network A.

**Acceptance Scenarios**:

1. **Given** all services (MongoDB, Redis, RabbitMQ, Temporal) are running via docker-compose, **When** a valid `IngestionRequestPayload` is submitted to the Ingestion API, **Then** a Job ID is persisted in MongoDB and a Decomposition Workflow is started in Temporal.
2. **Given** a Decomposition Workflow is running, **When** the workflow executes its activities (Job Setup → Heavy Processing → Manifest), **Then** mock chunk files and a `manifest.json` are written to the configured Proxy Data Outbox and Proxy Manifest Outbox folders respectively.
3. **Given** files appear in the outbox folders, **When** the Proxy Event Listener in Network B detects them via RabbitMQ messages, **Then** the Assembly Workflow receives signals for each chunk and the manifest.
4. **Given** all expected chunks and the manifest signal have arrived, **When** the Assembly Workflow triggers assembly activities, **Then** mock reassembly executes, a CSV report is generated, and a `StatusCallbackPayload` with `JobStatus: COMPLETED` is dispatched to Network A's Callback Receiver.
5. **Given** the Callback Receiver receives the final status, **When** it processes the payload, **Then** it signals the Decomposition Workflow to close, and the job state is updated in MongoDB.

---

### User Story 2 - Proxy Failure and Retry Flow (Priority: P2)

A developer observes the retry path: a simulated proxy failure (`.ERROR.txt` file arriving in Network B) triggers Network B to notify Network A, which re-queues the failed chunk for a retry attempt via the Dispatch & Retry Service.

**Why this priority**: Retry infrastructure is a core architectural requirement. Validating this path proves the signal-based retry mechanism between networks is correctly wired.

**Independent Test**: Manually drop a `.ERROR.txt` file (named after a chunk) into the Network B inbox directory and verify that Network A's Callback Receiver receives the error notification, the Decomposition Workflow signal is received, the Dispatch & Retry Service re-places the chunk into the outbox, and the retry counter in MongoDB is incremented.

**Acceptance Scenarios**:

1. **Given** a transfer is in progress, **When** a `.ERROR.txt` file is detected by the Proxy Event Listener, **Then** an immediate HTTP retry request is sent to Network A's Callback Receiver.
2. **Given** the retry policy limit has not been exhausted, **When** Network A's Dispatch & Retry Service is triggered, **Then** the failed chunk is re-placed in the Proxy Data Outbox and the retry counter is updated in MongoDB.
3. **Given** the retry policy limit is exhausted for a chunk, **When** the Dispatch & Retry Service determines no more retries are allowed, **Then** a `.HARDFAIL.txt` command file is written to the Proxy outbox for Network B to process.
4. **Given** a `.HARDFAIL.txt` file arrives in Network B, **When** the Assembly Workflow processes it, **Then** the affected file is marked as permanently failed and the final CSV report reflects this status.

---

### User Story 3 - Infrastructure Connectivity Verification (Priority: P3)

A developer starts all services cold and verifies that each microservice successfully connects to its required infrastructure (MongoDB, Redis, RabbitMQ, Temporal) on startup, with clear health/ready indicators.

**Why this priority**: Before testing business flows, infrastructure connectivity must be independently verifiable to isolate configuration from logic issues.

**Independent Test**: Start all services. Check startup logs and health endpoints (where applicable) to confirm MongoDB connection, Redis connection, RabbitMQ channel establishment, and Temporal worker registration are all successful with no errors.

**Acceptance Scenarios**:

1. **Given** docker-compose is running all external services, **When** the Ingestion API starts, **Then** logs confirm successful MongoDB connection and RabbitMQ consumer/producer registration.
2. **Given** Temporal server is running, **When** any Workflow or Activity worker service starts, **Then** the Temporal worker registers its task queue and the service appears as active in the Temporal UI.
3. **Given** the Job Setup Activity worker starts, **When** it initializes, **Then** logs confirm successful Redis connection.
4. **Given** a misconfigured connection string is provided, **When** a service starts, **Then** it fails fast with a clear, descriptive error message and non-zero exit code.

---

### Edge Cases

- What happens when a Temporal Workflow times out waiting for all chunks to arrive? The workflow must transition to `TIMEOUT` status and the Reporting Service must dispatch a `StatusCallbackPayload` with `JobStatus: TIMEOUT`.
- What happens when the Ingestion API receives a payload missing required fields? The API must return a `400 Bad Request` with a validation error list before creating any job state.
- What happens when a `.UNSUPPORTED.txt` file arrives in Network B? The Proxy Event Listener must signal the Assembly Workflow to flag that file as `NOT_SUPPORTED` (not retried), which is then reflected in the final CSV report.
- What happens when a service crashes mid-workflow? Temporal's event history must allow the workflow to resume from the last successful activity on service restart.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The project MUST be cleaned of all default template boilerplate (sample controllers, weather forecasts, placeholder code) before any POC code is written.
- **FR-002**: All shared data models (`IngestionRequestPayload`, `StatusCallbackPayload`, `JobStatus` enum, `AnswerType` enum) MUST be defined in the `Shared.Contracts` project exactly as specified in the model definitions and MUST NOT be altered.
- **FR-003**: The system MUST define interfaces for all cross-service interactions (IJobRepository, IProxyConfigRepository, IAssemblyBlueprintRepository, ICallbackService, etc.) in `Shared.Contracts` or the appropriate project.
- **FR-004**: The Ingestion API MUST accept the `IngestionRequestPayload` via HTTP POST, validate all required fields, persist the initial job record to MongoDB, and start a Temporal Decomposition Workflow.
- **FR-005**: The Ingestion API MUST also support RabbitMQ as an ingestion channel, consuming messages that contain the `IngestionRequestPayload` JSON.
- **FR-006**: The Decomposition Workflow (Network A) MUST orchestrate activities in the correct sequence: Job Setup → Heavy Processing (Decompose & Split) → Manifest generation → await completion signals.
- **FR-007**: The Job Setup Activity MUST fetch proxy configuration rules from Redis (with MongoDB as the backing store) and return a workflow configuration object; the actual business logic MUST be replaced with a mock that returns a hardcoded configuration matching the proxy-requirements CSV structure.
- **FR-008**: The Heavy Processing Activity MUST accept source path info from the workflow, write mock chunk files to the configured Proxy Data Outbox directory using the naming convention `{jobId}_chunk_{n}.bin`, and return a `DecompositionMetadata` object describing the full file→convertedFile→chunk hierarchy (including `originalFormat`, `appliedConversion`, and `originalRelativePath` per file); actual split/conversion logic MUST be replaced with pseudo-code comments. The number of mock chunks generated MUST be read from a `MockChunkCount` configuration value (environment variable or config file) — this value represents the number of chunks **per converted file** (so a file with 2 converted outputs produces `2 × MockChunkCount` total chunks). No hardcoded chunk count is permitted, as real chunk counts are driven by package size and proxy-imposed size limits and can be arbitrarily large.
- **FR-009**: The Manifest Activity MUST accept the `DecompositionMetadata` object from the workflow, generate a manifest file named `{jobId}_manifest.json`, and write it directly to the configured Proxy Manifest Outbox directory; actual manifest schema construction logic MUST be replaced with a mock that writes a structurally valid JSON file conforming to the hierarchical schema defined in `data-model.md` section 3.2. The schema MUST include: `jobId`, `packageType`, `originalPackageName`, `targetPath`, `totalChunks`, and a `files` array where each entry contains `originalRelativePath`, `originalFormat`, `appliedConversion`, and a `convertedFiles` array, each with a `convertedRelativePath` and `chunks` array of `{ name, index, checksum }`. This hierarchical structure gives Network B everything it needs to reverse conversions, reassemble files in order, reconstruct the directory tree, and re-pack archives.
- **FR-010**: The Dispatch & Retry Activity MUST handle retry signals from the workflow, re-place the identified chunk into the Proxy Data Outbox, increment the retry count in MongoDB, and generate a `.HARDFAIL.txt` command file when the retry limit is exhausted.
- **FR-011**: The Callback Receiver (Network A) MUST expose an HTTP endpoint to receive `StatusCallbackPayload` objects from Network B and forward them as Temporal signals to the running Decomposition Workflow.
- **FR-012**: The Proxy Event Listener (Network B) MUST consume RabbitMQ messages from the configured Proxy queue. Each message body MUST be a JSON object `{ "filePath": "<absolute-or-relative-path-in-Network-B>" }` with Content-Type `application/json`. The listener MUST extract the `jobId` from the filename by taking the substring before the first `_` character (e.g., `3fa85f64` from `3fa85f64_chunk_1.bin`), and MUST use `SignalWithStart` with workflow ID `assembly-{jobId}` to atomically start or signal the Assembly Workflow. This naming convention ensures the jobId is always available regardless of file arrival order.
- **FR-013**: The Proxy Event Listener MUST distinguish between `.ERROR.txt`, `.UNSUPPORTED.txt`, `.HARDFAIL.txt`, manifest files (ending in `_manifest.json`), and data chunks by inspecting the filename of `filePath`, and route each type to the corresponding Assembly Workflow signal or Network A HTTP callback. Chunks and manifests both use `SignalWithStart`; `.ERROR.txt` files trigger an HTTP retry request to Network A without signalling the workflow.
- **FR-014**: The Assembly Workflow (Network B) MUST use `Workflow.WaitConditionAsync` to first wait for the manifest signal, then wait until the resolved chunk count equals `totalChunks` from the manifest. The resolved count is: `receivedChunks + hardFailedChunks + unsupportedChunks`. Hard-failed and unsupported chunks permanently close their slot and must count toward the total so the workflow is never blocked waiting for a chunk that will never arrive normally.
- **FR-015**: The Assembly Workflow MUST enforce a configurable global timeout using `Workflow.WaitConditionAsync(() => condition, timeoutDuration)`. When the timeout fires (the method returns `false`), the workflow MUST still execute the Reporting & Dispatch Activity with `JobStatus.Timeout` before returning — this is an internal workflow timer, not `WorkflowRunTimeout`. Setting `WorkflowRunTimeout` would kill the workflow process and prevent any cleanup activity from running.
- **FR-016**: The Manifest & State Activity (Network B) MUST parse the arriving `{jobId}_manifest.json` using the hierarchical schema defined in `data-model.md` section 3.2, persist the full `AssemblyBlueprint` (including the complete `files` hierarchy, `packageType`, `targetPath`, and `totalChunks`) to Network B's MongoDB instance, and return the `AssemblyBlueprint` to the workflow; actual parsing logic MUST be mocked.
- **FR-017**: The Reassembly & Validation Activity MUST be triggered once all chunks have arrived and MUST follow the reversal algorithm defined in `data-model.md` section 3.3: for each file in the blueprint, concatenate its chunks in `index` order per converted file, reverse the `appliedConversion` to restore the original format, write the result to `originalRelativePath` under `targetPath`, and if `packageType` is an archive type re-pack all files into the original archive; actual concatenation, format reversal, and re-packing logic MUST be replaced with pseudo-code comments. The activity MUST return an array of per-file statuses (`COMPLETED`, `FAILED`, `NOT_SUPPORTED`) keyed by `originalRelativePath` for use in the CSV report.
- **FR-018**: The Reporting & Dispatch Activity MUST generate the final CSV summary file (matching the `output.csv` format: `status,dir_path` columns), construct the `StatusCallbackPayload`, deliver it via RabbitMQ or File System based on `answerType`, and send the HTTP acknowledgment to Network A.
- **FR-019**: All services MUST read their configuration (connection strings, outbox directory paths, retry limits, queue names) from environment variables or a configuration file, with no hardcoded values.
- **FR-020**: Every dependency injected into a class MUST be registered and consumed via an interface (e.g., `IJobRepository` rather than `JobRepository`). No concrete implementation class may be injected directly; all DI bindings must use interface types as the service key.
- **FR-021**: Each service MUST emit structured startup log lines confirming connectivity for each infrastructure dependency it uses (MongoDB, Redis, RabbitMQ, Temporal — where applicable). Example format: `[INFO] <ServiceName>: <Dependency> connected successfully`. No `/health` HTTP endpoint is required; log output is the sole observability mechanism for startup connectivity in this POC.

### Key Entities

- **IngestionRequestPayload**: The entry contract for new transfer jobs. Fields: `callingSystemId`, `callingSystemName`, `sourcePath`, `targetPath`, `targetNetwork`, `externalId`, `answerType` (enum: RABBITMQ | FILE_SYSTEM), `answerLocation`. This model is fixed and cannot be modified.
- **StatusCallbackPayload**: The exit contract returned to the originating system. Extends IngestionRequestPayload with: `JobId`, `UpdateDate`, `JobCount`, `OrigJobId`, `JobStatus` (enum: COMPLETED | COMPLETED_PARTIALLY | FAILED | TIMEOUT | INTERNAL_ERROR). This model is fixed and cannot be modified.
- **Job**: Internal tracking record persisted in Network A's MongoDB. Contains the original payload, internal `JobId`, current status, retry counts per chunk, and timestamps.
- **ProxyConfiguration**: Cached in Redis, backed by MongoDB in Network A. Represents per-file-type rules: source format, required conversion, maximum size limit (matching the proxy-requirements CSV structure).
- **Manifest**: The JSON blueprint written to the Proxy Manifest Outbox as `{jobId}_manifest.json`. Uses a three-level hierarchical schema — see `data-model.md` section 3.2 for the full schema and field definitions. Root fields: `jobId`, `packageType`, `originalPackageName`, `targetPath`, `totalChunks`, `files[]`. Each file entry carries `originalRelativePath`, `originalFormat`, `appliedConversion`, and a `convertedFiles[]` array. Each converted file carries a `convertedRelativePath` and `chunks[]`. Each chunk carries `name`, `index`, `checksum`. Together these fields give Network B the complete reversal plan: reassemble chunks in order, reverse format conversions, reconstruct the directory tree, and re-pack the original archive if applicable.
- **AssemblyBlueprint**: Persisted in Network B's MongoDB. Derived from the parsed manifest, represents the expected package: expected chunk list, per-file status tracking, and checksums.
- **ChunkSignal**: Temporal signal payload carrying the file path of an arrived chunk in Network B.
- **CsvReport**: The final output file generated by Network B, with columns `status` and `dir_path`, one row per file in the original package.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A complete end-to-end test run (submitting one ingestion request through the HTTP API) results in a `StatusCallbackPayload` with `JobStatus: COMPLETED` being delivered to the configured answer location, with all steps logged and traceable in Temporal UI — achievable within 60 seconds of submission.
- **SC-002**: A developer can start the entire system (all microservices + docker-compose) with a single command or a documented sequence of at most 3 commands. All services must emit structured startup log lines confirming each infrastructure connection (e.g., `[INFO] MongoDB connected`, `[INFO] Temporal worker registered`). No `/health` HTTP endpoint is required.
- **SC-003**: The retry flow can be demonstrated by manually triggering an `.ERROR.txt` event, and the system correctly re-queues the chunk and delivers a final status reflecting the retry outcome.
- **SC-004**: All 14 microservices compile and start without errors against the configured infrastructure.
- **SC-005**: The final CSV report generated by Network B exactly matches the `output.csv` format (`status,dir_path` with valid `JobStatus` values) for a given mock package.
- **SC-006**: No hardcoded connection strings, directory paths, or credentials exist anywhere in the codebase; all configuration is externalized.

## Clarifications

### Session 2026-03-08

- Q: What is the `manifest.json` schema that Network A writes (FR-009) and Network B parses (FR-016)? → A: A three-level hierarchical schema — `{ "jobId", "packageType", "originalPackageName", "targetPath", "totalChunks", "files": [{ "originalRelativePath", "originalFormat", "appliedConversion", "convertedFiles": [{ "convertedRelativePath", "chunks": [{ "name", "index", "checksum" }] }] }] }`. See `data-model.md` section 3.2 for the full annotated schema and a worked example. The file is named `{jobId}_manifest.json`.
- Q: How does the Proxy Event Listener know which Assembly Workflow to signal when a chunk arrives before the manifest? → A: Every file written to the outbox is prefixed with the `jobId` (e.g., `3fa85f64_chunk_1.bin`, `3fa85f64_manifest.json`). The Proxy Event Listener extracts the `jobId` as the substring before the first `_` and calls `SignalWithStart` on `assembly-{jobId}`. The Assembly Workflow accumulates chunk signals in its state; when the manifest arrives later and sets `_expectedChunks`, the workflow's `WaitConditionAsync` immediately resolves if all chunks have already been received.
- Q: What is the RabbitMQ message envelope format for Proxy Event Listener messages (FR-012/FR-013)? → A: Minimal JSON envelope — `{ "filePath": "/network-b/inbox/chunk1.bin" }`, Content-Type: `application/json`; routing (ERROR / UNSUPPORTED / manifest / data chunk) is determined by inspecting the `filePath` value.
- Q: How many mock chunks does the Heavy Processing Activity produce per job? → A: Configurable — chunk count is driven by package size and proxy limitations and can be large; the mock MUST read a `MockChunkCount` value from configuration (no hardcoded count).
- Q: Should HTTP endpoints (Ingestion API, Callback Receiver) require authentication for the POC? → A: No authentication — all HTTP endpoints are explicitly unsecured for the POC; authentication is out of scope and deferred to a later phase.
- Q: Is a formal `/health` HTTP endpoint required per service, or is startup log output sufficient for SC-002? → A: Startup logs only — structured log lines (e.g., `[INFO] MongoDB connected`, `[INFO] Temporal worker registered`) satisfy SC-002; no `/health` endpoint is required.

---

## Assumptions

- The Proxy itself (the actual air-gap system) is simulated by a developer or test harness manually copying files between the Network A outbox directories and Network B inbox directories, or by a simple folder-watcher script — it is not implemented as part of this POC.
- For the POC, Network A and Network B will run on the same machine but with logically separate configurations (separate MongoDB databases, separate RabbitMQ queues/exchanges, separate directory paths) to simulate the air gap.
- The `.specify` and `temp` directories are not part of the production project and will not be included in any build or Docker configuration.
- HTTP endpoint authentication (API keys, JWT, mTLS) is explicitly out of scope for this POC. All HTTP-facing endpoints (Ingestion API, Callback Receiver) are unsecured. Authentication will be addressed in a subsequent phase.
- All retry policies will be simple configurable integers (max retry count) stored in configuration files for the POC — no complex policy engine is required.
- The Temporal server, MongoDB, Redis, and RabbitMQ instances are all provided by the existing `docker-compose.yml` and no additional external infrastructure is needed.
- For the POC, a single RabbitMQ instance serves both networks using distinct exchanges and queues to simulate cross-network messaging.
