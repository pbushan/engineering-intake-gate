# MVP Release Gate

This document records the deterministic first-UI MVP release decision for Controlled Dry Run deployment. `PASS` means only that an intake has enough relevant evidence and context for Engineering to begin investigation without avoidable clarification. It does not confirm a defect, ownership, priority, severity, cause, solution, commitment, or customer assertion.

Canonical command:

```sh
./test-harness/test-release-gate.sh
```

The command restores and builds; runs backend unit, integration, strategic migration, acceptance, Product-route RBAC, and CSRF tests; runs frontend contract, type, lint, unit/component, complete major-route accessibility, production-build, and package-audit checks; records bundle sizes; and enforces source security, typed-browser-boundary, and Product/test topology constraints. Product Compose then proves fresh profileless startup, Bootstrap Admin, session continuity through backend recreation, the complete onboarding contract, encrypted credential recovery, governed operations, Health/Audit/Home, configured-backend and UI-only restarts, and cross-surface canary leakage checks. The deterministic engine harness retains explicit legacy import, immutable generation, scheduler, saved-query, Dry Run, Production rejection, reconciliation, and no-mutation proof. It writes `TestResults/release-gate/summary.json` plus `frontend-bundles.txt` and exits non-zero on any failure. No real PAT, OpenAI credential, Anthropic credential, or provider application call is required; network access is limited to dependency/image acquisition when artifacts are not already local.

## Production/LIVE authorization posture

The current repository authorization record is this document's `MVP RELEASE DECISION: READY_FOR_CONTROLLED_DRY_RUN`. It does **not** authorize Production/LIVE activation.

LIVE scenarios in the deterministic release gate prove revision protection, audit-before-write, narrow mutation, duplicate suppression, and reconciliation against the purpose-built mock ADO service. Optional real-provider smoke/calibration paths are explicit and DRY_RUN-only. Neither is a Production approval mechanism.

The UI and backend management APIs keep Production (the user-facing name for existing `LIVE`) unavailable until this release/live-readiness decision is explicitly changed. UI presence, successful deterministic tests, configuration capability, or a hidden browser control is insufficient. Operational requests accept no mode and execute only the active immutable generation. Internal mock-LIVE regression coverage remains test-only and is not an authorization mechanism; see [ADR 0002](adr/0002-ui-control-plane-contract.md).

## Locked acceptance criteria

| Requirement | Description | Automated Test | Result | Notes |
|---|---|---|---|---|
| AC-01 | Complete ticket produces revision-safe PASS enforcement. | `AC_01_SAFE_002_LivePassUsesOneRevisionGuardedTagWriteThenOneMarkedComment`; mock-LIVE acceptance | PASS | Intake-only wording and marker asserted. |
| AC-02 | Incomplete ticket produces revision-safe FAIL enforcement. | `AC_02_IncompleteTicketFailsAndEnforcesOnlyConfiguredIntakeState` | PASS | HTTP through full pipeline and stateful writer double. |
| AC-03 | Corrected ticket transitions FAIL to PASS. | `AC_03_CorrectedTicketTransitionsFromFailToPassAndRetainsHistory` | PASS | Prior comment retained. |
| AC-04 | Unchanged FAIL does not duplicate its comment. | `AC_04_UnchangedFailureDoesNotDuplicateCommentOrTagMutation` | PASS | Audit-backed signature; no no-op tag write. |
| AC-05 | Changed FAIL deficiencies create a current-gap comment. | `AC_05_ChangedDeficienciesCreateANewCommentWithoutNoOpTagWrite` | PASS | Criterion ordering/prose do not define identity. |
| AC-06 | AI outage produces ERROR and zero enforcement. | `AC_06_RepeatedTransientProviderFailureIsAuditedAsErrorWithNoMutations`; permanent failure test | PASS | No guessed FAIL/PASS. |
| AC-07 | Partial ADO mutation reconciles without unnecessary AI rerun. | `AC_07_E10_PendingCommentIsReconciledWithoutEvaluationOrDuplicateTag`; Docker partial-write injection | PASS | Completed tag operation is not replayed. |
| AC-08 | DRY_RUN audits exact proposals and performs zero ADO writes. | `DRY_001_DRY_002_DRY_003_DRY_004_NFR_007_*`; Docker request ledger | PASS | Writer endpoints absent in DRY_RUN ledger. |
| AC-09 | Supported attachment-only evidence reaches evaluation. | `CNT_004_CNT_008_AC_09_AC_23_E6_*`; multimodal adapter theories | PASS | Text and visual paths covered offline. |
| AC-10 | Validator comments are marked and excluded from later evidence. | `CNT_002_CNT_003_AC_10_OnlyValidMachineMarkedCommentsAreExcluded` | PASS | Exact canonical marker only. |
| AC-11 | Material ambiguity results in actionable FAIL. | `AC_11_AmbiguityOnlyFailRendersAnActionableClarificationWithoutInventingDeficiency` | PASS | No invented deficiency. |
| AC-12 | Explained Unknown may pass; unexplained Unknown fails. | `AI_002_AI_003_AC_11_AC_12_AC_13_ScriptedContractScenariosAreAccepted` | PASS | Deterministic provider scenarios. |
| AC-13 | Unable-to-reproduce may pass with alternative evidence. | `AI_002_AI_003_AC_11_AC_12_AC_13_ScriptedContractScenariosAreAccepted` | PASS | Contract acceptance, not real-model calibration. |
| AC-14 | Configured critical exclusion is NOT_ELIGIBLE. | `DISC_005_AC_14_AC_24_ConfiguredExclusionIsNotEligibleAndNeverEvaluated`; Docker item 103 | PASS | Generic configured field/value only. |
| AC-15 | Recognizable secrets are redacted before AI. | `SEC_001_AC_15_E5_RecognizableSecretsAreRedactedWithoutDestroyingBenignWords`; provider-body tests | PASS | Values absent from response/log/audit/database checks. |
| AC-16 | Eligible manual item traverses the governed pipeline. | `MAN_002_AC_16_EndpointEvaluatesEligibleItemAndReturnsOnlySafeDryRunSummary`; Docker item 101 | PASS | Query membership and exclusions precede AI. |
| AC-17 | Restart retains checkpoint, registrations, and audit. | `DISC_003_DISC_004_AC_17_RegistrationAndCheckpointPersistAcrossRestartInOneDurableBoundary`; Docker restart | PASS | Named SQLite volume retained. |
| AC-18 | OpenAI and Anthropic satisfy one application contract. | `AC_18_Phase4ValidationAndPhase5AuditArePortableAcrossConfiguredProviders` | PASS | Mocked transports; no real AI required. |
| AC-19 | Different profile/policy sources can be explicitly imported into fresh deployments without code or image rebuild. | `AC_19_DifferentProfileAndPolicyLoadWithoutCodeChanges`; Docker same-image fresh-volume imports | PASS | Import intentionally rejects replacement in an already configured singleton deployment. |
| AC-20 | Revision race produces zero stale writes. | `SAFE_001_AC_20_E1_StaleRevisionAfterEvaluationCausesZeroWritesAndAuditsRequeue`; E2 conflict test | PASS | Re-evaluation required. |
| AC-21 | Query failure leaves checkpoint unchanged. | `E8_AC_21_QueryFailureLeavesExistingCheckpointUnchangedAndDoesNotRegisterOrProcess` | PASS | Failure is not empty membership. |
| AC-22 | Database failure prevents unsafe mutation. | `ERR_004_AC_22_E9_PersistenceFailureAfterEvaluationBlocksAllLiveWrites` | PASS | No false success. |
| AC-23 | Unsupported/partial/unavailable evidence is disclosed. | `CNT_005_AC_23_UnsupportedAndUnavailableAttachmentsAreExplicit`; bounded PDF tests | PASS | Semantic decision remains at AI boundary. |
| AC-24 | Manual out-of-scope item is NOT_ELIGIBLE. | `MAN_003_AC_24_EndpointCannotBypassQueryAndInvalidIdIsRejected` | PASS | No item read or AI call. |

## Locked edge cases

| Requirement | Description | Automated Test | Result | Notes |
|---|---|---|---|---|
| E1 | Revision changes after evaluation. | `SAFE_001_AC_20_E1_StaleRevisionAfterEvaluationCausesZeroWritesAndAuditsRequeue` | PASS | Zero writes. |
| E2 | Provider reports a mutation concurrency conflict. | `SAFE_001_AC_20_E2_ProviderConcurrencyConflictIsClassifiedAndNeverRetriedBlindly` | PASS | One guarded request only. |
| E3 | PASS conflicts with failed criteria. | `E3_AI_007_ContradictoryOutcomesAreRejectedAndNeverTrusted`; Docker `e3` | PASS | Retry exhausts to ERROR. |
| E4 | Attachment disappears or fails during retrieval. | `E4_AttachmentDownloadFailureIsUnavailableAndDoesNotCrashEvidencePipeline`; Docker item 110 | PASS | Pipeline continues with disclosure. |
| E5 | Secret-bearing evidence is encountered. | `SEC_001_AC_15_E5_RecognizableSecretsAreRedactedWithoutDestroyingBenignWords` | PASS | Category/count only retained. |
| E6 | Necessary evidence is only in an unsupported attachment. | `CNT_004_CNT_008_AC_09_AC_23_E6_UnsupportedEvidenceCanInformSemanticFailButNeverHardCodesIt` | PASS | Fake AI supplies semantic FAIL. |
| E7 | Large/multi-page PDF exceeds inspection bounds. | `CNT_006_CNT_007_E7_PdfLeadingPageInspectionIsBoundedAndDisclosed` | PASS | Leading-page sampling disclosed. |
| E8 | Saved-query execution fails. | `E8_AC_21_QueryFailureLeavesExistingCheckpointUnchangedAndDoesNotRegisterOrProcess` | PASS | Checkpoint unchanged. |
| E9 | Persistence fails before safe mutation boundary. | `ERR_004_AC_22_E9_PersistenceFailureAfterEvaluationBlocksAllLiveWrites` | PASS | Zero enforcement. |
| E10 | Tag succeeds and comment fails or has an uncertain outcome. | `AC_07_E10_PendingCommentIsReconciledWithoutEvaluationOrDuplicateTag`; `E10_UncertainCommentOutcomeUsesMarkerInsteadOfReposting` | PASS | Marker resolves uncertainty. |
| E11 | Human removes the incomplete tag. | `E11_HumanRemovalIsAuthoritativeUntilANewNormalEvaluation` | PASS | No enforcement loop. |
| E12 | Human adds the validated tag. | `E12_ManualValidatedTagDoesNotSuppressLegitimateEvaluation` | PASS | Audit, not tag, proves validation. |
| E13 | Both configured intake tags are present. | `E13_ContradictoryIntakeTagsConvergeToTheTrustedDecision` | PASS | Trusted decision converges tags; unrelated tag survives. |
| E14 | Validator-like comment contains a malformed marker. | `E14_MalformedValidatorMarkerIsNotTreatedAsApplicationHistory` | PASS | Retained as human evidence. |
| E15 | Previously failed item leaves the query. | `E15_ItemLeavingQueryStopsEvaluationWithoutRemovingHistoricalState` | PASS | NOT_ELIGIBLE; history untouched. |
| E16 | Previously failed item becomes excluded. | `E16_ItemBecomingExcludedStopsEvaluationWithoutRemovingHistoricalState` | PASS | NOT_ELIGIBLE; history untouched. |
| E17 | Two executions contend for the same profile/database. | `DISC_007_SAFE_003_E17_OnlyOneProcessCanHoldLiveLeaseAndExpiredLeaseIsRecoverable` | PASS | One lease; stale lease recovers. |
| E18 | Schedule crosses daylight-saving boundaries. | `NFR_004_E18_DST_ConfiguredTorontoScheduleUsesLocalElevenPmAcrossStandardAndDaylightDates` | PASS | Local 23:00 remains local. |
| E19 | Azure DevOps rich text/HTML is supplied. | `CNT_001_E19_RawGenericWorkItemAndArbitraryFieldsBecomeEvidence`; HTML integration tests | PASS | Safe semantic normalization. |
| E20 | Provider repeatedly returns unsafe/unparseable output. | `AI_007_E20_MalformedMissingUnknownAndForbiddenPropertiesAreRejected`; Docker `e20` | PASS | ERROR and no enforcement. |

## Locked MUST and relevant NFR coverage

| Requirement | Description | Automated Test | Result | Notes |
|---|---|---|---|---|
| ARCH-001 | PASS semantics remain intake-completeness only. | Prompt and comment-renderer contract tests | PASS | User-facing wording asserted. |
| ARCH-002 | Generic code is team-neutral. | `ARCH_002_ProductionSourceHasNoTeamSpecificCoupling`; source scan | PASS | Organization-specific behavior is absent from `src`. |
| ARCH-003 | One ASP.NET Core host/container. | `ARCH_003_SolutionHasExactlyOneExecutableProject`; Compose service assertion | PASS | Mock ADO is a test double, not an application process. |
| ARCH-004 | One profile and one saved query per instance. | YAML validation and Docker profile endpoint | PASS | Singular models. |
| ARCH-005 | Domain/Application dependency direction is clean. | `ARCH_005_DomainAndApplicationHaveOnlyAllowedDependencies`; source gate | PASS | No provider/ASP.NET SDK types. |
| SAFE-001 | Unsafe evaluation/mutation state cannot enforce. | stale, persistence, AI error, exclusion tests | PASS | Fail closed. |
| SAFE-002 | Invalid AI output never becomes guessed PASS. | E3/E20 contract and Docker tests | PASS | Deterministic validation. |
| SAFE-003 | Stale/unresolved/persistence-unsafe state blocks enforcement. | AC-20, AC-22, E17 tests | PASS | SQLite lease and reconciliation boundary. |
| AI-001 | AI scope is advisory semantic evaluation only. | request/prompt contract tests | PASS | No mutation capability. |
| AI-002 | Prohibited determinations and ADO actions are excluded. | `AI_005_*`; `AI_006_*` | PASS | Closed schema. |
| AI-003 | Provider response remains untrusted structured input. | provider adapter/parser tests | PASS | No adapter repair. |
| AI-004 | Requests are grounded in sanitized evidence. | `AI_001_AI_004_*`; leakage tests | PASS | Raw item excluded. |
| AI-005 | Prompt prohibits classification/cause/solution/ownership/severity. | `AI_005_PromptPreservesIntakeOnlyGroundedAndPartialEvidenceContract` | PASS | Locked language asserted. |
| AI-006 | AI has no mutation authority. | `AI_006_ProviderAndTrustedResultExposeNoMutationAuthority` | PASS | Reflection boundary test. |
| AI-007 | Retry/error behavior is bounded and provider-neutral. | provider failure and E3/E20 tests | PASS | Application owns retry. |
| AI-008 | Model discovery is assistive, normalized, and cannot erase a confirmed selection. | `AI_MGMT_001_*`; `AI_MGMT_006_*`; Docker deterministic management path | PASS | OpenAI/Anthropic Models APIs; manual fallback remains available. |
| AI-009 | Provider credentials use explicit safe secret slots and revision-aware verification. | `AI_MGMT_003_*`; `AI_MGMT_004_*`; `AI_MGMT_007_*` | PASS | No secret redisplay or raw diagnostics. |
| AI-010 | Model confirmation is server-validated and bound to actor, provider, settings, and credential revision. | `AI_MGMT_004_*`; `AI_MGMT_006_*`; Docker confirmation/restart path | PASS | Opaque, expiring, single-use candidates. |
| AI-011 | AI persistence preserves singleton/profile identity and does not hot-activate runtime services. | `AI_MGMT_003_*`; `AI_MGMT_005_*`; Docker activation block | PASS | Configuration changes require validated generation activation. |
| ADO-001 | Read source and narrow writer are application-owned ports. | architecture/source/writer integration tests | PASS | HTTP details isolated. |
| ADO-002 | Allowed writes are deterministic, guarded, idempotent, audited. | Mock-LIVE HTTP acceptance | PASS | Tags/comments only; product activation remains Dry Run-only. |
| DISC-001 | Saved query defines governed population. | saved-query pagination/dedup and Docker tests | PASS | No alternate population. |
| DISC-002 | Initial discovery applies UTC lookback. | `DISC_002_DISC_003_InitialBootstrapUsesConfiguredLookbackAndRegistersOnlyRecentQueryMembers` | PASS | Missing changed-time is not guessed. |
| DISC-003 | Work is registered before processing. | SQLite discovery restart test | PASS | Durable boundary. |
| DISC-004 | Checkpoint advances atomically with registration. | SQLite transaction/restart test | PASS | No early checkpoint. |
| DISC-005 | Exclusions are generic and configured. | eligibility and AC-14 tests | PASS | Missing field does not match. |
| DISC-006 | Current incomplete tag controls re-evaluation. | `DISC_006_E11_*` | PASS | Human removal honored. |
| DISC-007 | Pending/processing/error work survives restart. | AC-17 repository restart tests | PASS | Recoverable registrations. |
| DISC-008 | Query failure differs from empty success. | AC-21/E8 tests | PASS | No inferred absence. |
| MAN-001 | Operator Run Now uses incremental workflow. | incremental endpoint/service and Docker tests | PASS | Same path as scheduled trigger. |
| MAN-002 | Operator can evaluate one governed item. | AC-16 endpoint/Docker tests | PASS | Manual work-item trigger audited. |
| MAN-003 | Manual execution cannot bypass governance. | AC-24 endpoint tests | PASS | Query/exclusion checks first. |
| CFG-001 | Deployment behavior is externalized. | SQLite authority, explicit-import, and Compose tests | PASS | Environment contains credential references only. |
| CFG-002 | Secrets remain outside source control/config. | ignore/deployment tests and credential scan | PASS | No real keys/PATs. |
| CFG-003 | Intake policy is machine-readable and validated. | shared YAML-import and SQLite-load validation tests | PASS | Stable criterion IDs. |
| CFG-004 | Human-readable standard URL is required. | invalid-profile tests | PASS | Validated URI. |
| CFG-005 | Exactly one saved query is configured. | invalid-profile tests | PASS | Singular UUID. |
| CFG-006 | Zero or one active profile is resolved. | singleton repository, profileless host, and Docker tests | PASS | No multi-profile host or fallback. |
| DRY-001 | DRY_RUN executes the complete implemented pipeline. | full dry-run acceptance and Docker | PASS | Through audit. |
| DRY-002 | DRY_RUN is first-class durable audit state. | restart acceptance | PASS | Explicit enum. |
| DRY-003 | Exact proposals are returned and stored. | decision/audit tests | PASS | Closed vocabulary/order. |
| DRY-004 | Proposals never become external state in DRY_RUN. | dry-run service and request-ledger tests | PASS | Zero writes. |
| AUD-001 | Evaluation audit is safe and reconstructable. | SQLite reconstruction/restart tests | PASS | No raw evidence/response. |
| AUD-002 | Run audit retains safe aggregate state. | incremental SQLite audit test | PASS | UTC/provider-neutral usage. |
| AUD-003 | Required trigger types are modeled. | `AUD_003_AllRequiredTriggerTypesAreModeled` | PASS | Exactly three. |
| AUD-005 | Policy/model/prompt traceability is durable. | profile and audit reconstruction tests | PASS | Fingerprint included. |
| CNT-001 | Useful work-item context is preserved transiently. | source mapping and evidence tests | PASS | Generic fields/comments/relations. |
| CNT-002 | Validator comments have stable markers. | renderer/writer/marker tests | PASS | Canonical lowercase UUID. |
| CNT-003 | Self-generated comments are excluded. | AC-10 and diagnostic tests | PASS | Author display name irrelevant. |
| CNT-004 | Attachment content/status reaches evaluation. | attachment pipeline/adapter tests | PASS | Provider-neutral. |
| CNT-005 | Locked MVP attachment types are handled safely. | common format processor tests | PASS | No OCR/Office/archive claims. |
| CNT-006 | Evidence resource limits are deterministic. | count/byte/character/PDF tests | PASS | Shared total budget. |
| CNT-007 | Partial processing is disclosed. | AC-23/E7/audit tests | PASS | Omitted vs unavailable distinguished. |
| CNT-008 | Attachment failures do not hard-code a decision. | E4/E6 tests | PASS | Semantic evaluator decides sufficiency. |
| DB-001 | SQLite is operational state and readiness dependency. | SQLite/readiness and Docker tests | PASS | Mounted durable path. |
| DB-002 | Persistence uses Application-owned contracts. | architecture and repository tests | PASS | Infrastructure implementation. |
| PERSIST-001 | SQLite persistence survives restart. | repository and Docker restart tests | PASS | No PostgreSQL runtime. |
| SEC-001 | Credentials/evidence secrets are not exposed. | redaction/provider-body/Docker/source scans | PASS | Synthetic canaries absent. |
| SEC-002 | Detected values are absent from evidence/log/audit. | complete-pipeline leakage tests | PASS | Only safe metadata. |
| LOG-001 | Startup/readiness/shutdown logging is structured and safe. | Docker `ApplicationStarted` and canary checks | PASS | No credentials/raw evidence. |
| LOG-002 | Workflow stages emit safe structured events. | captured diagnostic logging tests | PASS | Stable event names. |
| DEP-001 | Single host builds as Docker image. | Docker build/harness | PASS | Multi-stage image. |
| DEP-002 | Runtime/harness is portable to supported local/Linux environments. | release build and POSIX Docker harness | PASS | No host-specific product logic. |
| DEP-003 | Container restart retains operational state. | Docker named-volume restart | PASS | Runtime and evaluation audit retained. |
| NFR-001 | .NET/Linux runtime portability. | Release build and Docker harness | PASS | External paths. |
| NFR-002 | Layering supports provider/profile portability. | architecture and AC-18/19 tests | PASS | No speculative infrastructure. |
| NFR-003 | Durable timestamps/state survive restart. | SQLite/reconciliation/Docker restart tests | PASS | UTC persisted. |
| NFR-004 | Mutation outcomes are durable and confirmed. | mutation audit/reconciliation tests | PASS | No pre-confirmation success. |
| NFR-005 | Evidence processing is explainable. | disclosure/audit tests | PASS | Safe statuses and counts. |
| NFR-006 | Raw/self-generated/secret evidence cannot cross trust boundary. | security and endpoint leakage tests | PASS | Visual limitation disclosed. |
| NFR-007 | Audit reconstructs decisions without raw inputs. | Evaluation and reconciliation restart tests | PASS | Safe structured state only. |
| NFR-008 | Usage/cost accounting is provider-neutral. | cost and adapter audit tests | PASS | Missing pricing stays null. |
| NFR-009 | Solution/harness remains maintainable. | full regression/format/release harness | PASS | Focused MVP only. |
| TEST-001 | Default suites are deterministic and offline. | all default test projects and Docker doubles | PASS | Live tests opt-in. |
| OPS-001 | Safety, logging, audit, usage, and errors are deterministic. | full regression and audit tests | PASS | Provider-neutral categories. |
| SCOPE-001 | No distributed/deferred infrastructure is introduced. | architecture/source/Compose audits | PASS | One host and SQLite. |
| ERR-001 | AI failures are technical ERROR. | AC-06 tests | PASS | No intake FAIL substitution. |
| ERR-002 | Retries are bounded at owning boundaries. | provider/read/reconciliation retry tests | PASS | No blind mutation retry. |
| ERR-003 | Query failure cannot advance discovery. | AC-21/E8 tests | PASS | Error audit. |
| ERR-004 | Persistence failure blocks enforcement. | AC-22/E9 tests | PASS | Fail closed. |
| ERR-005 | Partial mutation remains durable/reconcilable. | AC-07/E10 deterministic reconciliation tests | PASS | No duplicate confirmed operation. |

## Audit conclusions

- Security: PASS. Generic production source contains no organization-specific logic, committed credentials, customer data, or hard-coded live query/model/customer/severity policy. Local Admin/Viewer authorization protects control-plane data and mutations; cookie-authenticated state changes require antiforgery validation. Local credentials use AES-256-GCM with separately persisted installation material, while a distinct standard ASP.NET Data Protection key ring retains sessions across backend recreation. Environment sources persist names only, replacement resets verification state atomically, and verification success is committed only against the same credential revision. AI model lists and candidate tokens remain transient; security/configuration audit stores bounded actor-aware metadata only. Authorization construction for external providers remains isolated to provider adapters. Raw work items, evidence text, attachment/image/PDF bytes, raw provider payloads, submitted passwords, password hashes, plaintext credentials, ciphertext, encryption keys, cookies, antiforgery tokens, and detected secret values are absent from inappropriate responses, logs, and audit by contract and leakage tests.
- Architecture: PASS. There is one ASP.NET Core deployable. Domain and Application have no ASP.NET, Azure DevOps, OpenAI, Anthropic, or provider SDK dependencies. AI contracts cannot carry arbitrary ADO actions; deterministic Application code owns the closed mutation mapping. SQLite remains the sole operational persistence boundary. No distributed infrastructure was added.
- Fail-safe/idempotency: PASS. AI outage/malformed/contradictory output, query failure, persistence failure, stale revision, exclusion, unresolved concurrency, and partial mutation are covered with zero unsafe enforcement. Repeated executions suppress no-op tags, equivalent FAIL/PASS comments, completed reconciliation work, and checkpoint duplication.
- Crash/restart: PASS. Profileless, post-bootstrap, mid-wizard, post-credential, post-finalization, post-execution, post-generation-update, UI-only, and backend-only restart/recreation points are covered across Product Compose and deterministic tests. Checkpoint/registration/audit/run/generation persistence, pending reconciliation, expired-lease recovery, local-secret decryption, setup progress, and sessions are retained. Missing/malformed credential keys and corrupt ciphertext fail safely without credential deletion.

Notable direct runtime dependencies are `Cronos 0.11.0`, `AngleSharp 1.8.0`, `Microsoft.AspNetCore.OpenApi 10.0.9`, `Microsoft.Data.Sqlite 10.0.9`, `Microsoft.OpenApi 2.7.6`, `PdfPig 0.1.16`, `SixLabors.ImageSharp 3.1.12`, `SQLitePCLRaw.bundle_e_sqlite3 2.1.13`, and `YamlDotNet 18.1.0`. The release command found no known vulnerable direct or transitive package.

Optional real-AI smoke: NOT EXECUTED as part of the canonical gate.

Optional real-ADO read smoke: NOT EXECUTED. It remains opt-in and read-only and is not part of this decision. No real ADO mutation is run automatically.

MVP RELEASE DECISION: READY_FOR_CONTROLLED_DRY_RUN

FIRST UI MVP: READY_FOR_CONTROLLED_DRY_RUN
