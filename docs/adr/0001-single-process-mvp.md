# ADR 0001: Single-process MVP host

- **Status:** Accepted
- **Date:** 2026-09-10

## Decision

The MVP runs operator API endpoints, hosted scheduler/background processing, and intake orchestration inside one ASP.NET Core process and one application container. Hosted background work uses ASP.NET Core hosted-service patterns when introduced.

## Context

- The current runtime has one configured profile and one saved Azure DevOps query. ADR 0002 extends the UI-MVP target to zero or one logical profile without changing this single-host decision.
- SQLite is the MVP operational database and needs straightforward durable ownership.
- There is no MVP requirement to independently scale API and background work.
- Simpler deployment, testing, recovery, and local development are valuable.
- Distributed coordination is unnecessary for the MVP.

## Consequences

### Positive

- Simpler Docker topology.
- Simpler local development.
- Fewer failure modes and operational dependencies.
- Easier SQLite ownership and recovery model.
- A straightforward operational model for one profile/query deployment.

### Trade-offs

- API and background workloads cannot scale independently in MVP.
- A process failure affects both HTTP and scheduled functionality.

## Future

Domain and Application behavior must remain independent of the Host and provider adapters so API and background execution can be separated later if operational scale demonstrably requires it. This ADR does not authorize pre-emptive worker deployables, microservices, queues, event buses, distributed locks, or other distributed infrastructure.
