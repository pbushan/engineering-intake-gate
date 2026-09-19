# Engineering Intake Gate 2026.9.4

This release adds end-to-end Estimated AI Cost accounting while preserving the Controlled Dry Run safety posture.

## Highlights

- Reuses the existing provider-neutral OpenAI and Anthropic pricing service, SQLite cache, reviewed catalog, and seven-day freshness TTL.
- Captures every provider interaction separately so retries and future multi-request evaluations contribute their own token usage and estimate.
- Prefers provider-reported model metadata, then the request model, then captured profile configuration for costing.
- Persists exact decimal input, output, and total USD estimates with provider/model and pricing provenance; these are estimates, not provider-billed charges.
- Preserves evaluation success when pricing refresh, usage metadata, provider, or model pricing is unavailable.
- Uses the last successfully cached quote when refresh fails and records stale-price use internally.
- Aggregates interaction cost into evaluation cost, evaluation cost into run cost, and persisted evaluation cost into the existing Home 7/30/90-day windows without double counting.
- Distinguishes a known zero from unavailable (`—`) and partial estimates in APIs and UI.
- Populates Home, Recent Runs, run history, Run Details, evaluation Usage / cost, and evaluation detail from persisted estimates.
- Removes the duplicate run-level Estimated AI Cost field from Run Outcome Summary.
- Leaves historical records without persisted estimates unchanged and never backfills or reprices them.

## Compatibility and persistence

The public v1 contracts gain additive cost-completeness and coverage fields. Per-interaction accounting is stored additively in existing audit JSON, so schema 15 remains current and existing databases require no destructive migration. Legacy profile pricing fields remain import-compatible but are not used as the authority for new estimates.

## Verification

- Backend unit coverage for exact decimal arithmetic, input-only/output-only/zero usage, actual/request model precedence, multiple requests/retries, unknown pricing, unsupported providers, missing usage, partial aggregation, and the existing pricing TTL/stale-cache cases.
- Integration coverage for audit snapshot reload, Home 7/30/90 and half-open boundary semantics, exact aggregation, unknown historical records, and partial coverage.
- Acceptance coverage for structured unknown/coverage API behavior and OpenAPI additions.
- Frontend component coverage for known zero, unknown, partial, precision, window switching, Recent Runs, exactly one run-level cost field, and evaluation Usage / cost.
- Complete backend, frontend, formatting, security, OpenAPI, build, accessibility, package-audit, migration, and Docker release gates.

Cached-token accounting, administrator pricing overrides, negotiated pricing workflows, invoice/billing reconciliation, historical backfill, taxes, and currency conversion remain intentionally deferred.
