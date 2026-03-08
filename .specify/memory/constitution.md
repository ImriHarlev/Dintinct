<!--
SYNC IMPACT REPORT
==================
Version change: [TEMPLATE] → 1.0.0
Added principles:
  - I. Zero Tests (NON-NEGOTIABLE)
  - II. Clean Code
  - III. Organized Directory Structure
  - IV. Technology Stack
  - V. HTTP via ASP.NET Core Controllers
  - VI. Shared Project Boundaries
Added sections:
  - Project Layout
  - Governance
Removed sections: N/A (first real version from template)
Templates updated:
  ✅ .specify/templates/spec-template.md — "User Scenarios & Testing" section remains but tests are opt-in per principle I
  ✅ .specify/templates/tasks-template.md — test tasks already marked OPTIONAL; principle I supersedes
  ✅ .specify/templates/plan-template.md — Testing field MUST reflect "None (no tests unless explicitly requested)"
Deferred TODOs: None
-->

# Dintinct POC Constitution

## Core Principles

### I. Zero Tests (NON-NEGOTIABLE — SUPERSEDES ALL OTHER GUIDANCE)

No tests of any kind MUST be written, scaffolded, or referenced unless the user explicitly requests
them in the current conversation. This means:

- No unit tests
- No integration tests
- No end-to-end (e2e) tests
- No test projects, test helpers, or test fixtures
- No xUnit, NUnit, MSTest, Playwright, or any other test framework references

This principle overrides every other source of guidance, including template suggestions,
framework conventions, and AI instincts. When in doubt: **do not add tests**.

When the user does request tests, their exact scope MUST be confirmed before implementation.

### II. Clean Code

All C# code MUST be readable, minimal, and purposeful:

- Methods MUST have a single, clear responsibility.
- Classes MUST be cohesive — no god objects.
- Names MUST be descriptive and intention-revealing; abbreviations are prohibited unless they are
  universally understood domain terms (e.g., `DTO`, `API`).
- Dead code, commented-out blocks, and speculative generality MUST NOT be committed.
- Code comments MUST explain *why*, never *what* — the code itself explains what.
- Magic strings and magic numbers MUST be extracted to named constants or configuration.
- YAGNI: do not add abstractions or indirection until there is a concrete need.

### III. Organized Directory Structure

The established project layout MUST be respected and kept clean:

```
src/
├── NetworkA/
│   ├── NetworkA.Ingestion.API/          # ASP.NET Core Web API (entry point)
│   ├── NetworkA.Decomposition.Workflow/ # Temporal Workflow worker
│   ├── NetworkA.Callback.Receiver/      # Temporal signal/callback handler
│   └── Activities/
│       ├── NetworkA.Activities.Dispatch/
│       ├── NetworkA.Activities.JobSetup/
│       ├── NetworkA.Activities.Manifest/
│       └── NetworkA.Activities.HeavyProcessing/
├── NetworkB/
│   ├── NetworkB.ProxyListener.Service/  # Temporal worker / listener
│   ├── NetworkB.Assembly.Workflow/      # Temporal Workflow worker
│   └── Activities/
│       ├── NetworkB.Activities.Reporting/
│       ├── NetworkB.Activities.HeavyAssembly/
│       └── NetworkB.Activities.ManifestState/
└── Shared/
    ├── Shared.Contracts/                # DTOs and shared interfaces
    └── Shared.Infrastructure/           # Temporal client, MongoDB, Redis helpers
```

New projects MUST follow the naming convention `<Network>.<Role>.<Type>` and be placed under the
correct network or `Shared` folder. No new top-level folders MUST be created without explicit
approval.

### IV. Technology Stack

The project MUST use:

- **Runtime**: .NET 10
- **Language**: C# (latest language version enabled by .NET 10)
- **Workflow orchestration**: Temporal .NET SDK (`Temporalio`)
- **Storage**: MongoDB (connection managed via `Shared.Infrastructure`)
- **Cache/messaging**: Redis (configuration managed via `Shared.Infrastructure`)
- **HTTP framework**: ASP.NET Core (minimal hosting model with controller routing)

No alternative ORMs, workflow engines, or infrastructure libraries MUST be introduced without
explicit approval. NuGet package versions MUST be kept consistent across projects via
`Directory.Packages.props` or a shared MSBuild props file when feasible.

### V. HTTP via ASP.NET Core Controllers

All HTTP endpoints MUST be implemented as ASP.NET Core controller actions:

- Controllers MUST inherit from `ControllerBase`.
- Minimal API (`app.Map*`) style MUST NOT be used for new endpoints.
- Each controller MUST be focused on a single resource or bounded context.
- Route templates MUST follow REST conventions (`/api/v1/[resource]`).
- Input validation MUST use data annotations or `FluentValidation`; validation logic MUST NOT
  live inside controller action bodies.
- Controllers MUST delegate business logic to services or Temporal workflows — no business
  logic inside controller methods.

### VI. Shared Project Boundaries

The two shared projects have strict, non-overlapping responsibilities:

**`Shared.Contracts`** is the *language* of the system:

- Contains only DTOs, enums, and interfaces that cross network or service boundaries.
- MUST NOT reference any infrastructure library (no MongoDB drivers, no Redis, no Temporal SDK).
- Both `NetworkA` and `NetworkB` projects MAY reference `Shared.Contracts`.

**`Shared.Infrastructure`** is the *plumbing* of the system:

- Contains Temporal client factory/helpers, MongoDB connection setup, and Redis configuration.
- Shared infrastructure code goes here exactly once — it MUST NOT be duplicated in individual
  worker projects.
- Worker and API projects that need infrastructure MUST reference `Shared.Infrastructure`.
- `Shared.Infrastructure` MAY reference `Shared.Contracts` but NOT the reverse.

## Project Layout

```
Dintinct_Poc/
├── src/                         # All production source code (see Principle III)
├── .specify/                    # Speckit planning artifacts
│   ├── memory/constitution.md   # This file
│   └── templates/               # Command templates
├── specs/                       # Per-feature plans, specs, tasks
└── Dintinct_Poc.sln             # Solution file
```

No `tests/` directory MUST exist unless the user explicitly requests tests (Principle I).

## Governance

- This constitution supersedes ALL other guidance, templates, framework defaults, and AI
  conventions. When any source of guidance conflicts with this document, this document wins.
- Principle I (Zero Tests) is absolute and MUST be re-read before any task that involves
  scaffolding, code generation, or library setup.
- Amendments MUST be made by re-running `/speckit.constitution` with explicit instructions.
- `CONSTITUTION_VERSION` follows semantic versioning:
  - MAJOR: removal or redefinition of a principle.
  - MINOR: new principle or material expansion of guidance.
  - PATCH: clarification, wording, or non-semantic refinement.
- All feature plans generated by `/speckit.plan` MUST include a **Constitution Check** gate that
  verifies compliance with Principles I–VI before any implementation task begins.
- The **Testing** field in any plan MUST read: *"None — tests are prohibited unless explicitly
  requested (Principle I)"*.

**Version**: 1.0.0 | **Ratified**: 2026-03-08 | **Last Amended**: 2026-03-08
