# Attachment and Media Evidence

Engineering Intake Gate remains **Controlled Dry Run only**. Attachment processors produce bounded observational evidence for `intake-evaluation-v2`; they never decide PASS/FAIL and never mutate Azure DevOps.

## Secure Azure DevOps download

AttachedFile relations may use the documented organization-scoped form `https://dev.azure.com/{organization}/_apis/wit/attachments/{guid}` or the supported project-scoped variant. The downloader parses the URI and requires the configured scheme, host, effective port, organization, exact attachment path, and a canonical GUID. It rejects userinfo, fragments, traversal and encoded traversal, deceptive hosts, other organizations, extra path segments, duplicate query keys, and every query key except `api-version`, `fileName`, and `download`. The client forces API version 7.1 while preserving the existing declared-size, response-header, streaming, per-file, and aggregate limits.

## Supported matrix

| Type | Behavior |
|---|---|
| TXT, log | bounded normalized text |
| JSON | bounded parse with depth limit and canonical property ordering |
| XML | DTD/external resolution prohibited |
| CSV | bounded row sampling |
| PNG, JPEG | validated dimensions and transient image input |
| PDF | deterministic text extraction; pages without a useful text layer are rendered and observed visually within page/pixel/byte/time limits |
| XLSX | sheet names, bounded rows/columns/cells, cached/literal values, and literal formulas; formulas, macros, links, connections, and embedded objects are never executed |
| DOCX | bounded paragraph/table text; external resources and embedded content are not fetched or executed |
| MP3, WAV, M4A | ffprobe validation, bounded WAV preprocessing, timestamped transcription |
| MP4, MOV, WebM | probe, optional audio extraction/transcription, representative frame extraction, factual frame observation, and key screenshot selection |
| XLS | explicitly unsupported in this release; no legacy OLE parser is loaded |
| Archives, macro-enabled Office files, unknown binary | explicitly unsupported |

## PDF and media processing

PDF evidence records total/inspected pages, text-extracted pages, visually inspected pages, per-page provenance, truncation, and warnings. Failure to inspect a scanned page is a technical partial/unavailable state, not a claim that Support omitted evidence.

Media runs in a random, user-only temporary directory. `ffmpeg`, `ffprobe`, and `pdftoppm` are invoked directly with separated arguments—never through a shell—with cancellation, timeout, bounded output, exit-code checks, dimension/pixel/byte/duration limits, and process-tree termination. Original media, extracted audio, sampled frames, and chunks are deleted on success, failure, timeout, cancellation, and exception. Raw media is never persisted.

Video sampling combines first/last and fixed interval frames with bounded scene-change candidates. Duplicate content hashes are removed before model use. Frame analysis is constrained to factual visible observations and cannot decide PASS/FAIL, severity, priority, ownership, root cause, or solution.

Sub-stages retain independent status: probe, audio extraction, transcription, frame extraction, visual analysis, and screenshot selection. A successful transcript survives vision failure; successful visual observations survive transcription failure or a missing audio stream; partial frame/model results remain usable.

## Key video evidence

At most six screenshots per video are selected from already-inspected frames by deterministic relevance, scene-change, timestamp, duplicate, and byte-limit rules. They are stored in the existing `selected_key_screenshot_artifacts` architecture, scoped through their organization/project attachment artifact, and expire with the existing evidence-retention policy (30 days by default).

Evaluation detail exposes a responsive **Key Video Evidence** section with lazy thumbnails, source filename, original timestamp, associated observation, provenance, safe alt text, keyboard-operable enlargement, and explicit zero/expired states. The authenticated endpoint verifies that the requested screenshot belongs to the requested run/evaluation Analysis Context and returns bytes without exposing a filesystem path.

Screenshots are internal evidence only. They are never uploaded to Azure DevOps, added as AttachedFile relations, or embedded in comments.

## Reuse, Force Fresh, and cost

Normal rerun uses the Phase 1 cache. Composite artifact hits reuse transcript, visual observations, and selected screenshots. Transcript sub-artifacts key only on source content, media extraction/transcription provider/model/version/chunking, and transcript-relevant limits. Vision sub-artifacts key only on source content, sampling/image/vision versions and vision-relevant limits. A change in one does not invalidate the other. An unchanged complete evaluation makes zero provider calls.

Force Fresh uses the existing Admin action and bypasses all reusable document/media/evaluation work. It redownloads, re-extracts, retranscribes, re-observes, regenerates screenshots, and remains Dry Run with zero ADO writes.

New transcription, frame/scanned-PDF vision, and final-evaluator calls participate in current-run interaction accounting. Reuse contributes no new interaction or historical spend. Transcription pricing/usage that cannot be expressed by the token pricing catalog is recorded as incomplete/unknown, never as zero.

Cache persistence is an optimization. Successful current-run spreadsheet, transcript, PDF-vision, and video observations remain authoritative if artifact or screenshot persistence fails; a safe warning is exposed and the attachment is not downgraded solely because reuse could not be saved.

## Default hard limits

The profile attachment limits include raw per-file/aggregate bytes plus: 200 PDF pages, 20 spreadsheet sheets, 200 rows per sheet, 50 columns, 5,000 cells, 1,800 seconds of media, 4,096-pixel dimensions, 33,554,432 decoded pixels, 12 sampled frames, 5 MiB per frame, six selected screenshots, 3 MiB retained screenshot bytes per video, 100,000 transcript characters, 120 seconds per subprocess, and two concurrent media jobs. All values are configurable subject to validation; screenshot count has an immutable ceiling of six.
