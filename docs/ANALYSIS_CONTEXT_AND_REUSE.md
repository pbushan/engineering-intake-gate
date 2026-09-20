# Analysis Context, Evidence Retention, and Smart Rerun

Engineering Intake Gate remains **Controlled Dry Run only**. This capability changes what the product retains and reuses; it does not enable Azure DevOps writes.

## What is persisted

Each eligible evaluation creates a versioned `AnalysisContextSnapshot`: the bounded, normalized, secret-redacted work-item fields, description, evaluation tags and relations, included human comments and safe provenance, attachment manifest and normalized extracted findings, redaction/truncation disclosures, processing warnings, and profile/policy/prompt/version fingerprints actually supplied to the evaluator. Recognized Engineering Intake Gate comments are excluded before fingerprinting and persistence.

Each supported attachment may create an immutable `AttachmentEvidenceArtifact`, keyed by organization, project, attachment identity, SHA-256 content hash, processor/version, normalized-evidence schema, and processing-limit fingerprint. Filename is recorded and participates in the manifest, but is not attachment identity. The cache cannot cross organization/project scope.

The product still discards raw work-item/provider payloads, authorization data, credentials, raw attachment bytes, transient sampled frames, extracted audio, and temporary media data. Phase 2 now persists redacted timestamped transcripts, factual visual observations, frame-sampling/sub-stage metadata, and at most six selected key screenshots per video. Screenshots remain internal and are never uploaded to Azure DevOps.

## Retention and cleanup

Reusable normalized customer evidence expires after `audit.evidenceRetentionDays` (30 days by default; maximum 3650). Selected video screenshots use the same expiry. `audit.maximumSelectedVideoScreenshots` defaults to 6 and has a hard safety ceiling of 6; it may be configured downward.

Cleanup runs at startup and before analysis. It deletes expired context, attachment artifacts, evaluation-cache entries, and selected-screenshot references transactionally and idempotently. Long-lived run/evaluation audit remains valid and records the original evidence expiry even after reusable content is gone.

## Normal Rerun

Normal rerun is reuse-eligible:

- unchanged attachment content, processor version, schema, and limits reuses that attachment artifact;
- transcript and visual sub-artifacts use independent equivalence keys, so transcription-only changes preserve vision and vision/sampling-only changes preserve transcription;
- changed/additional/replaced attachments regenerate only affected artifacts;
- ticket, policy, prompt, provider/model, parser contract, profile, or evidence-manifest changes rerun evaluation;
- a fully equivalent, unexpired evaluation makes zero provider calls, records zero tokens and `$0` new AI cost, creates a new audit, and links to its originating run/evaluation.

ADO revision remains the future write-concurrency authority. Evaluation equivalence instead uses a semantic source fingerprint that excludes generated validator comments, application-owned intake tags, and revision/change metadata. Filename-only changes remain visible through the attachment manifest.

## Force Fresh Analysis

The evaluation screen offers Admins **Force Fresh Analysis** behind a confirmation that explains possible AI cost. It bypasses reusable attachment/evaluation entries, performs fresh processing and evaluation, records actual new usage/cost, and creates a normal audit. It is still a Dry Run and performs no Azure DevOps mutation.

## Inspection and cost semantics

Evaluation detail exposes the structured ticket summary for PASS and FAIL, analysis-context disclosures, per-attachment processing/reuse and media sub-stages, Key Video Evidence thumbnails/details, configured and provider-reported AI metadata, run interaction count, reuse origins, current-run cost, and the exact planned tag/comment mutation. Large normalized evidence is represented only by a bounded 2,000-character attachment preview; screenshot bytes use a scoped authenticated endpoint.

Current-run totals contain only newly incurred work. Historical cost is never copied into a reused run and no avoided cost is fabricated.

See [Attachment and Media Evidence](ATTACHMENT_MEDIA_EVIDENCE.md) for supported types, security controls, partial-success semantics, limits, and raw-media lifecycle.
