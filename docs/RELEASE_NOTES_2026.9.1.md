# Engineering Intake Gate 2026.9.1

## Actionable validation and licensing patch

Engineering Intake Gate `2026.9.1` makes user-correctable setup failures identify the affected fields and establishes the MIT License for the public repository.

## Changes

- Added the canonical MIT License at the repository root.
- Aligned first-party package metadata and public documentation with SPDX `MIT`.
- Added safe, structured field and section errors to the API contract without exposing submitted values, provider bodies, or internal exception details.
- Added an accessible validation summary with keyboard navigation, inline field guidance, first-error discovery, and automatic expansion of hidden evidence-limit errors.
- Extended actionable feedback across Profile & Policy, Schedule, saved-query, Azure DevOps, AI/model, Analyze Ticket, and Login, and confirmed the existing Bootstrap correction path, while preserving stale/conflict, authorization, provider, governance, and technical-error semantics.
- Preserved intake-engine and execution semantics; Controlled Dry Run remains unchanged and Production/LIVE remains unavailable.
