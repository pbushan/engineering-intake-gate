import ArrowBackRounded from '@mui/icons-material/ArrowBackRounded';
import ExpandMoreRounded from '@mui/icons-material/ExpandMoreRounded';
import RefreshRounded from '@mui/icons-material/RefreshRounded';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Grid,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material';
import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { RunItemDetail } from '../api/contracts';
import { AdminOnly } from '../auth/AuthContext';
import {
  AzureDevOpsLink,
  DecisionChip,
  EffectsCard,
  SafeErrors,
  UsageCost,
} from '../operations/presentation';

const decisionExplanation = {
  pass: 'Enough intake information is present to begin engineering investigation.',
  fail: 'Required intake information is missing or insufficient.',
  error: 'A technical or system error prevented a reliable assessment.',
  notEligible: 'This item is outside the configured intake-governance boundary.',
};

export function EvaluationDetailPage() {
  const { runId = '', evaluationId = '' } = useParams();
  const navigate = useNavigate();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [rerunning, setRerunning] = useState(false);
  const [confirmFresh, setConfirmFresh] = useState(false);
  const [rerunError, setRerunError] = useState<ApiError | null>(null);
  const viewKey = `${runId}|${evaluationId}|${refreshKey}`;
  const [result, setResult] = useState<{ key: string; detail: RunItemDetail | null; error: ApiError | null }>({ key: '', detail: null, error: null });
  const current = result.key === viewKey ? result : { key: viewKey, detail: null, error: null };
  const { detail, error } = current;

  useEffect(() => {
    const controller = new AbortController();
    void apiClient.getRunItem(runId, evaluationId, controller.signal).then((response) => {
      setResult({ key: viewKey, detail: response, error: null });
      requestAnimationFrame(() => headingRef.current?.focus());
    }).catch((caught: unknown) => {
      if (!(caught instanceof DOMException && caught.name === 'AbortError')) setResult({ key: viewKey, detail: null, error: asApiError(caught) });
    });
    return () => controller.abort();
  }, [evaluationId, runId, viewKey]);

  if (error) return <Stack spacing={2}><Button component={RouterLink} to={`/runs/${runId}`} startIcon={<ArrowBackRounded />} sx={{ alignSelf: 'flex-start' }}>Back to run</Button><Alert severity="error"><AlertTitle>Evaluation could not be loaded</AlertTitle>{error.message}<Box sx={{ mt: 1 }}><Button color="inherit" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Try again</Button></Box></Alert></Stack>;
  if (!detail) return <Stack spacing={2} aria-busy="true"><Typography role="status">Loading evaluation detail…</Typography><Skeleton variant="rounded" height={140} /><Skeleton variant="rounded" height={260} /><Skeleton variant="rounded" height={230} /></Stack>;

  const dryRun = detail.effectiveMode === 'dryRun';
  const rerun = async (forceFresh: boolean) => {
    if (rerunning) return;
    setRerunning(true);
    setRerunError(null);
    try {
      const response = await apiClient.analyzeWorkItem({ workItemId: Number(detail.workItemId), workItemUrl: null, forceFresh });
      if (response.evaluationId) void navigate(`/runs/${response.runId}/items/${response.evaluationId}`);
      else void navigate(`/runs/${response.runId}`);
    } catch (caught) {
      setRerunError(asApiError(caught));
    } finally {
      setRerunning(false);
      setConfirmFresh(false);
    }
  };
  return (
    <Stack spacing={3}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}>
        <Box>
          <Button component={RouterLink} to={`/runs/${detail.runId}`} startIcon={<ArrowBackRounded />} sx={{ mb: 1, ml: -1 }}>Back to run</Button>
          <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Work item {detail.workItemId}</Typography>
          <Typography component="h1" variant="h1" ref={headingRef} tabIndex={-1}>Evaluation detail</Typography>
          <Stack direction="row" spacing={1.5} sx={{ mt: 1.5, alignItems: 'center', flexWrap: 'wrap', gap: 1 }}><DecisionChip decision={detail.decision} /><Typography color="text.secondary">{decisionExplanation[detail.decision]}</Typography></Stack>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button variant="outlined" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Refresh</Button>
          <AdminOnly><Button variant="contained" disabled={rerunning} onClick={() => void rerun(false)}>Rerun</Button></AdminOnly>
          <AdminOnly><Button variant="outlined" color="warning" disabled={rerunning} onClick={() => setConfirmFresh(true)}>Force Fresh Analysis</Button></AdminOnly>
        </Stack>
      </Stack>

      {rerunError ? <Alert severity="error" role="alert"><AlertTitle>Analysis could not be started</AlertTitle>{rerunError.message}</Alert> : null}

      {detail.decision === 'notEligible' ? (
        <Alert severity="warning"><AlertTitle>Not Eligible</AlertTitle>{detail.eligibilityReason || 'This work item is outside the configured intake query or does not satisfy the current eligibility boundary.'}</Alert>
      ) : null}
      {detail.updateSuppressed ? (
        <Alert severity="info"><AlertTitle>Duplicate Update Suppressed</AlertTitle>{detail.suppressionReason || 'The assessment was materially unchanged, so another Azure DevOps update was not posted.'}</Alert>
      ) : null}
      <SafeErrors errors={detail.errors} />

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2" gutterBottom>Ticket Summary</Typography>
          <Grid container spacing={2.5}>
            <Grid size={{ xs: 12, md: 8 }}><Summary detail={detail} /></Grid>
            <Grid size={{ xs: 12, md: 4 }}><Typography variant="caption" color="text.secondary">Azure DevOps</Typography><Box sx={{ mt: 0.5 }}><AzureDevOpsLink href={detail.azureDevOpsUrl} workItemId={detail.workItemId} /></Box><Typography variant="caption" color="text.secondary" sx={{ mt: 2, display: 'block' }}>Eligibility</Typography><Typography sx={{ textTransform: 'capitalize' }}>{detail.eligibility.replace(/([A-Z])/g, ' $1')}</Typography></Grid>
          </Grid>
        </CardContent>
      </Card>

      <Card variant="outlined"><CardContent><Typography component="h2" variant="h2" gutterBottom>Analysis Context</Typography>{detail.analysisContext ? <Grid container spacing={2}><Metadata label="Revision analyzed" value={detail.analysisContext.evaluatedRevision} /><Metadata label="Human comments" value={`${detail.analysisContext.humanCommentsIncluded} of ${detail.analysisContext.humanCommentsAvailable} included; ${detail.analysisContext.generatedCommentsExcluded} generated comments excluded`} /><Metadata label="Attachments" value={`${detail.analysisContext.attachmentCount} included`} /><Metadata label="Source fields" value={detail.analysisContext.sourceFields.join(', ') || 'No additional fields'} /><Metadata label="Evidence versions" value={`${detail.analysisContext.schemaVersion} / ${detail.analysisContext.normalizedEvidenceSchemaVersion}`} /><Metadata label="Disclosure" value={`${detail.analysisContext.truncationOccurred ? 'Truncation occurred' : 'No truncation'}; ${detail.analysisContext.redactionOccurred ? `${detail.analysisContext.redactionCount} secret value(s) redacted` : 'no secret redaction'}`} /><Metadata label="Processing warnings" value={detail.analysisContext.processingWarnings.join('; ') || 'None'} /><Metadata label="Reusable until" value={new Date(detail.analysisContext.expiresAtUtc).toLocaleString()} /></Grid> : <Typography color="text.secondary">Reusable analysis context is unavailable or has expired; the audit record remains available.</Typography>}</CardContent></Card>

      <Card variant="outlined"><CardContent><Typography component="h2" variant="h2" gutterBottom>Attachment Processing</Typography>{(detail.attachmentProcessing ?? []).length ? <TableContainer><Table size="small" aria-label="Attachment processing"><TableHead><TableRow><TableCell>File</TableCell><TableCell>Type / size</TableCell><TableCell>Status</TableCell><TableCell>Processor</TableCell><TableCell>Reuse</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead><TableBody>{detail.attachmentProcessing!.map((item) => <TableRow key={item.attachmentId}><TableCell>{item.fileName}</TableCell><TableCell>{item.mediaType ?? 'unknown'} · {item.sizeBytes == null ? 'unknown size' : `${Number(item.sizeBytes).toLocaleString()} bytes`}</TableCell><TableCell>{item.status}{item.failureCategory ? ` — ${item.failureCategory}` : ''}</TableCell><TableCell>{item.processorIdentity ?? 'none'} {item.processorVersion ?? ''}<Typography variant="caption" sx={{ display: 'block' }}>{item.inspectionMode}{item.truncated ? ' · truncated' : ''}{item.sampled ? ' · sampled' : ''}</Typography></TableCell><TableCell>{item.cacheReused ? 'Reused' : 'Processed'}</TableCell><TableCell>{item.normalizedEvidencePreview || item.warnings.length ? <Box component="details"><Box component="summary" sx={{ cursor: 'pointer' }}>View bounded evidence</Box>{item.normalizedEvidencePreview ? <Box component="pre" sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxWidth: 520 }}>{item.normalizedEvidencePreview}</Box> : null}{item.warnings.length ? <Typography variant="caption">Warnings: {item.warnings.join('; ')}</Typography> : null}</Box> : 'None'}</TableCell></TableRow>)}</TableBody></Table></TableContainer> : <Typography color="text.secondary">No attachments were included.</Typography>}</CardContent></Card>

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2">Criteria results</Typography>
          {detail.satisfiedCriteria.length > 0 ? <Box sx={{ mt: 2 }}><Typography component="h3" variant="h3" gutterBottom>Satisfied criteria</Typography><Stack direction="row" sx={{ flexWrap: 'wrap', gap: 1 }}>{detail.satisfiedCriteria.map((criterion) => <Chip key={criterion} color="success" variant="outlined" label={`${criterion} — Satisfied`} />)}</Stack></Box> : null}
          {detail.missingCriteria.length > 0 ? (
            <Box sx={{ mt: 3 }}><Typography component="h3" variant="h3" gutterBottom>Missing intake information</Typography><TableContainer><Table size="small" aria-label="Missing criteria"><TableHead><TableRow><TableCell>Criterion</TableCell><TableCell>Status</TableCell><TableCell>Explanation</TableCell><TableCell>Required support action</TableCell></TableRow></TableHead><TableBody>{detail.missingCriteria.map((deficiency, index) => <TableRow key={`${deficiency.criterionId}-${index}`}><TableCell>{deficiency.criterionId}</TableCell><TableCell><Chip size="small" color="warning" label="Missing" /></TableCell><TableCell sx={{ whiteSpace: 'normal', overflowWrap: 'anywhere' }}>{deficiency.reason}</TableCell><TableCell sx={{ whiteSpace: 'normal', overflowWrap: 'anywhere' }}>{deficiency.requiredSupportAction}</TableCell></TableRow>)}</TableBody></Table></TableContainer></Box>
          ) : null}
          {detail.satisfiedCriteria.length === 0 && detail.missingCriteria.length === 0 ? <Typography color="text.secondary" sx={{ mt: 2 }}>No criterion results were recorded for this evaluation.</Typography> : null}
        </CardContent>
      </Card>

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2">Ambiguities</Typography>
          {detail.ambiguities.length > 0 ? <Stack spacing={1.5} sx={{ mt: 2 }}>{detail.ambiguities.map((ambiguity, index) => <Box key={`${ambiguity.criterionId ?? 'general'}-${index}`}><Typography component="h3" variant="h3">{ambiguity.criterionId || 'General ambiguity'}</Typography><Typography sx={{ mt: 0.5, overflowWrap: 'anywhere' }}>{ambiguity.description}</Typography><Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>Clarification: {ambiguity.requiredClarification}</Typography></Box>)}</Stack> : <Typography color="text.secondary" sx={{ mt: 2 }}>No ambiguities were recorded.</Typography>}
        </CardContent>
      </Card>

      <Grid container spacing={2.5} aria-label="Azure DevOps effects">
        <Grid size={{ xs: 12, md: 6 }}><EffectsCard title="Proposed ADO Changes" effects={detail.proposedEffects} emptyMessage="No Azure DevOps changes were proposed." /></Grid>
        <Grid size={{ xs: 12, md: 6 }}><EffectsCard title="Actual ADO Changes" effects={detail.actualEffects} emptyMessage="No confirmed Azure DevOps changes were recorded." dryRun={dryRun && detail.actualEffects.length === 0} /></Grid>
      </Grid>

      <Card variant="outlined"><CardContent><Typography component="h2" variant="h2" gutterBottom>Exact ADO Preview</Typography><Alert severity="info">Dry Run — no Azure DevOps changes were made.</Alert>{detail.plannedAdoMutation ? <Stack spacing={2} sx={{ mt: 2 }}><Typography>Tag additions: {detail.plannedAdoMutation.tagAdditions.join(', ') || 'None'}</Typography><Typography>Tag removals: {detail.plannedAdoMutation.tagRemovals.join(', ') || 'None'}</Typography><Typography>Future derived attachment uploads: {detail.plannedAdoMutation.futureDerivedAttachmentUploads.join(', ') || 'None'}</Typography><Box><Typography variant="caption" color="text.secondary">Exact comment body</Typography><Box component="pre" sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', p: 2, bgcolor: 'background.default', borderRadius: 1 }}>{detail.plannedAdoMutation.exactCommentBody || 'No comment planned.'}</Box></Box></Stack> : <Typography color="text.secondary" sx={{ mt: 2 }}>No persisted mutation plan is available for this historical evaluation.</Typography>}</CardContent></Card>

      <Card variant="outlined"><CardContent><Typography component="h2" variant="h2" gutterBottom>AI / Cost</Typography><UsageCost usage={detail.tokenUsage} cost={detail.estimatedCost} />{detail.ai ? <Stack spacing={0.5} sx={{ mt: 2 }}><Typography>Configured: {detail.ai.configuredProvider} / {detail.ai.configuredModel}</Typography><Typography>Provider-reported model: {detail.ai.providerReportedModel || 'Not reported'}</Typography><Typography>Prompt: {detail.ai.promptVersion}</Typography><Typography>Execution mode: {detail.ai.executionMode}</Typography><Typography>Run interactions: {detail.ai.runInteractions}</Typography><Typography>Attachment artifacts: {detail.ai.attachmentArtifactsReused} reused; {detail.ai.attachmentArtifactsRegenerated} regenerated</Typography><Typography>Evaluation reused: {detail.ai.evaluationReused ? `Yes${detail.ai.originEvaluationId ? ` — origin evaluation ${detail.ai.originEvaluationId}` : ''}${detail.ai.originRunId ? `, run ${detail.ai.originRunId}` : ''}` : 'No'}</Typography></Stack> : null}{detail.estimatedCost?.pricingIdentity ? <Typography variant="caption" color="text.secondary" sx={{ mt: 2, display: 'block' }}>Estimate basis: {detail.estimatedCost.pricingIdentity}</Typography> : null}</CardContent></Card>

      <Accordion disableGutters>
        <AccordionSummary expandIcon={<ExpandMoreRounded />}><Typography component="h2" variant="h3">Technical evaluation details</Typography></AccordionSummary>
        <AccordionDetails>
          <Grid container spacing={2}><Metadata label="Evaluation ID" value={detail.evaluationId} /><Metadata label="Run ID" value={detail.runId} /><Metadata label="Revision evaluated" value={detail.evaluatedRevision} /><Metadata label="Selection reason" value={detail.selectionReason} /><Metadata label="Configuration generation" value={detail.configurationGenerationId === null ? 'Historical generation unavailable' : String(detail.configurationGenerationId)} /><Metadata label="Policy version" value={detail.policyVersion} /><Metadata label="Prompt version" value={detail.promptVersion} /><Metadata label="Provider / model" value={`${detail.provider} / ${detail.model}`} /></Grid>
          {detail.applicableCriteria.length > 0 ? <Box sx={{ mt: 2 }}><Typography variant="caption" color="text.secondary">Applicable criterion IDs</Typography><Typography sx={{ overflowWrap: 'anywhere' }}>{detail.applicableCriteria.join(', ')}</Typography></Box> : null}
          {detail.mutationAttempts.length > 0 ? <Box sx={{ mt: 3 }}><Typography component="h3" variant="h3" gutterBottom>Mutation attempts</Typography><Stack component="ul" sx={{ pl: 2.5 }}>{detail.mutationAttempts.map((attempt, index) => <li key={`${attempt.type}-${index}`}>{attempt.type} · {attempt.target} · {attempt.succeeded ? 'Succeeded' : `Not completed${attempt.safeErrorCategory ? ` (${attempt.safeErrorCategory})` : ''}`}</li>)}</Stack></Box> : null}
        </AccordionDetails>
      </Accordion>

      <Dialog open={confirmFresh} onClose={() => setConfirmFresh(false)} aria-labelledby="force-fresh-title"><DialogTitle id="force-fresh-title">Force Fresh Analysis?</DialogTitle><DialogContent><DialogContentText>This bypasses reusable attachment evidence and the prior evaluation, so it may incur additional AI cost. It remains a Dry Run and will not change Azure DevOps.</DialogContentText></DialogContent><DialogActions><Button onClick={() => setConfirmFresh(false)}>Cancel</Button><Button variant="contained" color="warning" disabled={rerunning} onClick={() => void rerun(true)}>Force Fresh Analysis</Button></DialogActions></Dialog>
    </Stack>
  );
}

function Summary({ detail }: { detail: RunItemDetail }) {
  const summary = detail.ticketSummary;
  if (!summary) return <Typography sx={{ whiteSpace: 'pre-wrap' }}>{detail.engineeringSummary || 'No engineering summary was recorded.'}</Typography>;
  const rows: [string, string | null | undefined][] = [['Issue summary', summary.issueSummary], ['Expected behavior', summary.expectedBehavior], ['Actual behavior', summary.actualBehavior], ['Environment', summary.environment], ['Business impact', summary.businessImpact]];
  return <Stack spacing={1.5}>{rows.map(([label, value]) => value ? <Box key={label}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography sx={{ overflowWrap: 'anywhere' }}>{value}</Typography></Box> : null)}<SummaryList label="Reproduction steps" values={summary.reproductionSteps} ordered /><SummaryList label="Affected examples" values={summary.affectedExamples} /><SummaryList label="Attachment findings" values={summary.attachmentFindings} /><SummaryList label="Investigation warnings" values={summary.investigationWarnings} />{!summary.issueSummary && detail.engineeringSummary ? <Typography>{detail.engineeringSummary}</Typography> : null}</Stack>;
}

function SummaryList({ label, values, ordered = false }: { label: string; values: string[]; ordered?: boolean }) {
  if (!values.length) return null;
  return <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Box component={ordered ? 'ol' : 'ul'} sx={{ mt: 0.5, mb: 0, pl: 3 }}>{values.map((value, index) => <li key={`${label}-${index}`}>{value}</li>)}</Box></Box>;
}

function Metadata({ label, value }: { label: string; value: string }) {
  return <Grid size={{ xs: 12, sm: 6 }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography sx={{ overflowWrap: 'anywhere' }}>{value}</Typography></Grid>;
}
