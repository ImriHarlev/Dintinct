# Why Temporal?

This system runs long-lived, multi-step document processing pipelines. Temporal fits because:

- **Workflow state IS the database** — durably stores job input, activity results, and retry counters (no MongoDB/Redis needed)
- **Built-in retry + timeout semantics** — enforced per-activity via `WorkflowActivityConfig`
- **Deterministic workflow IDs** — `decomposition-{jobId}` allows callbacks to target the right workflow without a lookup table
- **Cross-network signaling** — `AssemblyWorkflow` is triggered by a signal from `DecompositionWorkflow`
- **Heartbeating** — detects stuck long-running activities automatically

## Alternatives

| Alternative | Trade-off |
|---|---|
| **Azure Durable Functions** | .NET-native, serverless — weaker replay model, harder to self-host |
| **Dapr Workflows** | Lighter, Kubernetes-native — less mature for complex sagas |
| **AWS Step Functions** | Fully managed — AWS lock-in, no local dev parity |
| **MassTransit Sagas** | Fits existing RabbitMQ — saga state requires an external DB (back to MongoDB) |
| **Netflix Conductor** | Temporal's predecessor — Temporal was built to fix its limitations |
| **Custom saga over RabbitMQ** | Full control — you'd rebuild durability, retries, and history manually |

**Bottom line:** For C# + RabbitMQ + cross-network orchestration with long-running activities, Dapr Workflows or MassTransit Sagas are the most realistic alternatives — but both require re-introducing a state store that Temporal eliminates.
