# ADR 0002: UI and control-plane contract

- **Status:** Accepted and implemented
- **Date:** 2026-09-12
- **Release posture:** Controlled Dry Run

## Context

The intake engine runs as one ASP.NET Core host and persists singleton configuration, immutable runtime generations, discovery, checkpoints, leases, reconciliation, local users, setup progress, encrypted credential records, run/evaluation history, and audit in SQLite.

The product needed an operator UI and setup/control plane without creating a second decision engine, configuration authority, runtime router, or unsafe route to Production.

## Decision

### Topology and trust boundary

- Keep one ASP.NET Core backend host/process/container for operator endpoints, orchestration, and hosted scheduling.
- Add one stateless React + TypeScript UI served by Nginx in a separate container.
- The UI communicates only with supported same-origin backend REST contracts described by OpenAPI.
- The browser never calls Azure DevOps, OpenAI, or Anthropic and never owns eligibility, evidence handling, policy, mutation, checkpoint, reconciliation, scheduling, or persistence decisions.
- Deterministic backend code retains all provider calls and mutations behind application-owned interfaces.

### Singleton profile and SQLite authority

- A deployment has zero or one logical profile and one configured saved query.
- Draft, Dry Run, and Production describe profile/runtime state; they are not separate profiles.
- Profile selection, routing, switching, cloning, deletion, archival, replacement, and multi-profile hosting are outside the MVP.
- SQLite is the runtime authority: `SQLite -> validated immutable generation -> execution`.
- YAML/files are accepted only through explicit one-time legacy import. Import must preserve `profile_id`, never enumerate or heuristically choose a profile, and never create runtime fallback after SQLite state exists.
- Profileless startup is healthy; setup readiness is separate from process/database health.

### Immutable activation and scheduling

- Every run captures one validated immutable configuration generation and exact credential revisions before provider work.
- A run started on generation N remains on N; activating N+1 affects only future runs.
- Run-scoped provider adapters are built from the captured generation.
- Scheduling remains in the existing host, uses Cronos plus `TimeZoneInfo`, has no catch-up behavior, and shares the SQLite profile lease with manual execution.
- The scheduler remains alive while profileless, Manual Only, and during safe reconfiguration.

### Saved-query governance

- The confirmed saved query is the sole governed population.
- Analyze Ticket accepts one positive ID or configured-boundary work-item URL, then verifies query membership and configured exclusions.
- Out-of-query or excluded items are Not Eligible, do not invoke AI, and propose no mutation.
- Query input is a GUID/ID or matching organization/project query URL; arbitrary WIQL is not supported.
- Query changes follow `resolve -> validate -> preview -> confirm -> rebaseline -> activate` and cannot reuse stale checkpoint state.

### Controlled Dry Run and Production

- Product language uses Controlled Dry Run for existing `DRY_RUN` semantics and Production for existing `LIVE` semantics.
- The current UI and backend management/activation paths expose only Controlled Dry Run.
- Production requires a future explicit release decision; hiding a UI control is not sufficient authorization.
- Mock LIVE-path tests verify safety behavior only and do not authorize real Azure DevOps writes.

### Outcomes and metrics

- `PASS` is displayed as **Engineering Ready**: enough intake information exists to begin investigation.
- `FAIL` is displayed as **Intake Incomplete**.
- `ERROR` is a technical/system failure, never Intake Incomplete.
- `NOT_ELIGIBLE` is a distinct governance result.
- Engineering-Ready Rate is exactly `PASS / (PASS + FAIL)`; all other states are excluded.

### Authentication, secrets, and diagnostics

- Local authentication supports Bootstrap Admin, Admin, Viewer, login/logout/session, own-password change, and CSRF protection.
- Viewer is read-only; backend authorization remains the security boundary.
- Credentials use encrypted local storage or an environment-variable reference. Plaintext values are never persisted or redisplayed.
- APIs, logs, audit, and UI exclude raw provider payloads, whole evidence, arbitrary Azure DevOps payloads/error bodies, attachment bytes, passwords, and key material.
- System Health composes persisted/derived state without provider polling. Admins may explicitly invoke existing connection tests; Viewer receives no Admin-only AI diagnostics.
- Audit is paged, filterable, read-only, and contains bounded safe metadata with actor attribution.

### Provider model discovery

- OpenAI and Anthropic credentials can be verified independently.
- Model discovery is dynamic, with server-side validation and explicit confirmation.
- Manual model-ID validation is the fallback.
- A transient discovery outage does not erase a confirmed model.
- Automatic provider switching, routing, and failover are out of scope.

### Operational execution and history

- Run Profile Now reuses the synchronous incremental workflow and SQLite lease.
- Analyze Ticket reuses the governed manual workflow; requests cannot override query or execution mode.
- Admin plus antiforgery is required to execute. Admin and Viewer can read safe history/details.
- Proposed effects, attempted/actual effects, suppression, usage/cost, and safe errors come from durable audit state.
- Operational contracts exclude evidence text, attachments, prompts/provider payloads, credentials, and serialized configuration snapshots.
- Execution remains request-bound and synchronous; no queue, cancellation, retry, or rerun control is added.

### Product deployment and deterministic harness

- Product Compose contains the real backend and stateless UI only. It starts profileless and uses one durable application volume.
- The deterministic test topology adds a mock Azure DevOps service and fake AI only through test/development configuration.
- Default and release-gate tests require no live Azure DevOps, OpenAI, Anthropic, internet dependency, or real credential.
- Optional live-provider smoke tests remain explicit diagnostics and are not part of the release decision.

## Consequences

- The UI can evolve as a replaceable stateless client without becoming a product authority.
- The single backend remains headless-capable and owns every safety-sensitive decision.
- Mutable administration requires run-scoped adapter construction and immutable generations, but not another worker or service.
- SQLite remains the MVP application database; this ADR does not authorize PostgreSQL, Redis, queues, Kubernetes, distributed locks, microservices, or IPC.
- Production remains unavailable until a future release decision explicitly changes that posture.

## Deliberate first-release deferrals

Document-to-policy generation, saved-query hierarchy browsing, multi-profile hosting, Entra ID/SSO, password recovery, cancel/retry/rerun/acknowledge controls, persistent logs, notifications, advanced analytics, budgets, out-of-query analysis, automated backup, destructive retention enforcement, Production activation, and cross-provider failover remain outside the first release.
