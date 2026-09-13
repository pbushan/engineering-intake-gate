# Engineering Intake Gate 2026.9.2

This release enables post-creation editing of the singleton Engineering Intake Gate profile while preserving the Controlled Dry Run safety posture.

## Highlights

- Adds an Admin-only **Profile / Configuration (Beta)** destination after setup completes.
- Reuses the setup wizard to load and edit every persisted profile and policy field, including criteria, exclusions, processing limits, AI pricing, schedule, and audit retention.
- Keeps the Azure DevOps organization, project, and profile ID immutable in the edit workflow while allowing a validated saved-query change.
- Preserves credential secrecy while supporting local encrypted or environment-reference replacement, verification, model discovery, and model confirmation.
- Rejects profile saves when credential-backed saved-query or AI model validation is stale.
- Continues to create immutable runtime generations and preserve active-run snapshot isolation and the previous active generation on activation failure.

## Verification

- Backend unit, integration, and acceptance suites.
- Frontend contract drift, type-check, lint, unit/component, accessibility, and production-build checks.
- Full repository Controlled Dry Run release gate.

Production/LIVE activation, organization/project changes, multi-profile support, and profile deletion or cloning remain out of scope.
