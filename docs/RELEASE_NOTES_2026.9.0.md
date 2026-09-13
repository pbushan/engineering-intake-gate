# Engineering Intake Gate 2026.9.0

## Controlled Dry Run MVP

Engineering Intake Gate `2026.9.0` is the first public portfolio release of the self-hosted Azure DevOps intake quality gate. It is ready for Controlled Dry Run evaluation; Production/LIVE remains unavailable.

## Major capabilities

- Guided first-run setup with restart-safe progress
- Azure DevOps saved-query validation, count, and first-10 preview
- OpenAI and Anthropic credential verification and model selection
- Configurable intake criteria, tags, limits, exclusions, and schedules
- Manual ticket analysis and Run Profile Now within saved-query governance
- Hourly, daily, weekly, custom, and Manual Only scheduling
- Engineering Ready, Intake Incomplete, Error, and Not Eligible outcomes
- Home summary, run/evaluation detail, System Health, and actor-aware Audit
- Proposed-versus-actual Azure DevOps effects, usage, and estimated cost visibility

## Architecture highlights

- One ASP.NET Core backend host/process/container
- One stateless React/TypeScript UI served by Nginx
- SQLite as the singleton profile/policy and operational-state authority
- Append-only immutable runtime configuration generations
- Run-scoped Azure DevOps and AI adapters bound to exact credential revisions
- Deterministic evidence normalization, secret redaction, AI-response validation, and decision handling
- Checked OpenAPI-generated frontend contracts

## Installation

Docker users need Git, Docker with Compose v2, a modern browser, and network access for the initial image/package build. Local .NET or Node installations are not required.

```bash
docker compose up -d --build
```

Open `http://127.0.0.1:8080`, bootstrap the initial Admin, and complete the setup wizard.

## Security and safety

- Controlled Dry Run is the only activatable mode.
- Manual and scheduled execution remain inside the confirmed saved-query boundary.
- Local Admin/Viewer RBAC and antiforgery are enforced server-side.
- Local credentials use AES-256-GCM and are never redisplayed.
- The browser never contacts Azure DevOps or AI providers.
- Invalid AI output, stale revisions, unsafe persistence, and unresolved mutation state fail safely without enforcement.
- The deterministic release gate covers RBAC/CSRF, migrations, restart/recovery, secret leakage, and Product Compose.

## Known limitations

- Production/LIVE is unavailable.
- One logical profile and one saved query per deployment.
- No Entra ID/SSO, MFA, or password recovery.
- No cancel/retry/rerun controls or queue-based execution.
- No out-of-query analysis, persistent logs UI, notifications, or advanced analytics.
- No automatic backup system.

A valid restore using locally encrypted credentials requires the matching SQLite application state and credential-encryption key. Preserve the Data Protection key ring if existing sessions must survive restore.
