# Horizontal Scaling

This architecture scales horizontally per layer with no code changes — only more worker processes/replicas.

## Temporal Workers (main lever)

Each activity worker polls a specific task queue. Run more instances → Temporal distributes tasks automatically.

| Worker | Task Queue | Scale reason |
|--------|-----------|--------------|
| `NetworkA.Activities.HeavyProcessing` | `heavy-processing-tasks` | CPU-bound, most expensive |
| `NetworkA.Activities.Manifest` | `manifest-tasks` | I/O-bound, independent |
| `NetworkB.Activities.HeavyAssembly` | `heavy-assembly-tasks` | CPU-bound |
| `NetworkB.Activities.ManifestState` | `manifest-assembly-tasks` | I/O-bound |
| `NetworkB.Activities.Reporting` | `callback-dispatch-tasks` | Fan-out dispatches |
| `NetworkA.Decomposition.Workflow` | `decomposition-workflow` | Orchestrator (rarely bottleneck) |
| `NetworkB.Assembly.Workflow` | `assembly-workflow` | Orchestrator (rarely bottleneck) |

Each queue is isolated — scale CPU-heavy workers independently from I/O-bound ones.

## Ingress (NetworkA.Ingestion.API)

Stateless ASP.NET Core service. Put a load balancer in front and run N replicas.

## RabbitMQ Consumers (NetworkB.ProxyListener.Service)

Competing consumers model — run multiple replicas, each picks up messages from the same queue.

## What doesn't need scaling

- **Temporal server** — already managed, handles routing internally
- **Workflow state** — lives in Temporal's Postgres, not in workers

## How to scale in practice

Docker Compose:

```yaml
services:
  heavy-processing-worker:
    build: ./src/NetworkA/NetworkA.Activities.HeavyProcessing
    replicas: 5   # scale this independently
  manifest-worker:
    build: ./src/NetworkA/NetworkA.Activities.Manifest
    replicas: 2
```

Kubernetes: set `replicas` per Deployment, or use HPA on CPU/queue depth metrics.

No code changes required — Temporal's task queue model handles worker discovery automatically.
