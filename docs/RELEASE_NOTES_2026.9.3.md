# Engineering Intake Gate 2026.9.3

This release adds portable Profile & Policy import/export while preserving the Controlled Dry Run safety posture.

## Highlights

- Exports the current Profile & Policy configuration to a readable, versioned JSON document.
- Imports version 1 documents through validation, an explicit replacement confirmation, and atomic persistence.
- Accepts incomplete imports into the existing persisted draft so the normal validation workflow identifies missing information.
- Uses a separate allow-listed portability contract and excludes credentials, secrets, generated profile identifiers, runtime state, cache data, and history.
- Rejects malformed JSON, wrong formats, unsupported versions, invalid types, invalid enum values, and malformed nested data without changing current configuration.
- Refreshes the editor immediately after import and retains imported state across reloads.

## Verification

- Backend unit, integration, and acceptance tests, including export safety, round-trip replacement, invalid-import atomicity, and persisted incomplete drafts.
- Frontend component tests for actions, file selection, validation errors, confirmation/cancel, success feedback, and incomplete imports.
- Frontend contract drift, type-check, lint, accessibility, and production-build checks.
- Full repository Controlled Dry Run release gate.

Profile merging, selective import, multiple named profiles, cloud storage, remote URLs, clipboard workflows, encrypted exports, and schema migrations remain out of scope.
