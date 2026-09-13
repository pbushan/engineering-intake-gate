# Engineering Rules

These rules apply to all implementation phases.

## Code and dependencies

- Use clear, idiomatic, cross-platform .NET code and keep changes focused on the requested phase.
- Keep Domain provider-neutral; keep Application free of ASP.NET HTTP and external SDK types; put HTTP/host composition and provider adapters at the edges.
- Introduce interfaces only at concrete boundaries: external providers, persistence, clock/side effects, or independently testable behavior. Do not add general frameworks for hypothetical needs.
- Use names that describe generic intake concepts. Team-specific terms belong in profiles, not namespaces, classes, database names, or generic code.
- Prefer explicit flow and typed, versioned contracts over reflection, hidden conventions, or unvalidated dynamic data.

## Configuration and secrets

- Store profile-specific policy, field mappings, tags, terminology, criteria, prompts, and saved-query references as external configuration. Maintain a generic example profile.
- For UI-MVP work, SQLite is the only target profile/policy runtime authority and must enforce zero-or-one logical profile. Treat YAML/files only as explicit one-time legacy import, preserve `profile_id`, use no folder-selection heuristics, and provide no YAML fallback after a SQLite profile exists.
- Capture one validated immutable configuration generation per run. A settings update cannot alter an active run; derive its ADO/AI adapters from its captured snapshot.
- Use supported .NET configuration and environment/secret injection for deployment settings. Never hard-code or commit credentials, tokens, private connection strings, or live query identifiers.
- Detect and redact secrets before AI requests. Do not reproduce secrets or raw sensitive evidence in logs, audit records, exceptions, fixtures, or test assertions.
- UI-managed credentials must be encrypted at rest and non-redisplayable. UI/API diagnostics expose safe state/verification metadata only, never plaintext, provider prompt/response bodies, whole evidence/ADO payloads, or attachment bytes.

## Decisions, providers, and mutations

- PASS means intake completeness only; never interpret it as defect confirmation, ownership, cause, solution, validated severity/priority, or fix commitment.
- Treat AI output as untrusted structured input. Deterministic code validates schema, policy compatibility, grounding/consistency rules, and permitted decision values before using it.
- AI adapters may not receive provider mutation capabilities. Azure DevOps adapters may perform only deterministic, explicitly allowed mutations supplied by validated application flow.
- Isolate ADO, AI, filesystem/network, and persistence SDK types inside Infrastructure adapters. Default tests use deterministic fakes/mocks, not live services.
- Protect mutations with current revision checks, idempotency keys/state, material-change comparison, audit, and reconciliation. If an error, stale revision, unsafe persistence state, or ambiguous prior mutation exists, make no enforcement mutation.
- Never infer or guess PASS after a failed or invalid evaluation.
- Keep the saved query authoritative for all manual and scheduled analysis. A query change requires validation/preview/confirmation, a new immutable generation, and safe rebaseline through the existing discovery/checkpoint mechanism.
- Do not expose Production/LIVE through UI or backend activation until the existing repository release decision explicitly approves it.

## Reliability, time, and observability

- Use UTC timestamps for persisted/audited time. Convert to local time only at presentation boundaries.
- Handle errors explicitly at boundaries; preserve safe actionable context while preventing secret/data leakage. Retries must be bounded, deterministic in policy, and safe for idempotent operations.
- User-correctable failures must identify the corrective action whenever the backend can safely determine it. Use stable field/section identifiers, preserve entered values, and keep non-validation failures semantically distinct.
- Emit structured logs with stable event names and safe correlation identifiers. Log decision/mutation outcomes and failure reasons, not credentials or unredacted source evidence.
- Keep operational SQLite state durable in deployment (mounted volume); do not treat the container filesystem as the database lifecycle.

## Tests, requirements, and delivery

- Write xUnit tests unless the repository later establishes a justified convention. Default tests must compile and run without internet access, live ADO, live AI, or external databases.
- Name or annotate tests with relevant requirement IDs where practical (for example `SAFE-001`) and update `docs/REQUIREMENTS_TRACEABILITY.md` when requirements move state.
- Keep opt-in live-provider tests separate from the default suite and make their credentials/configuration explicit.
- Keep Docker development cross-platform and maintain a single application container for MVP. Future Compose support may use local mocks/fake providers but must not create another production application process.
- Keep root `docker-compose.yml` as the only public Product Compose and independent of test doubles. Keep tracked test Compose files under `test-harness/compose/`, with deterministic mock-ADO/fake-AI harnesses offline and separate from opt-in live-provider smoke tests.
- Do not add provider SDKs, migrations, infrastructure, integrations, or features outside the requested phase. Record an extension boundary and defer it instead.
