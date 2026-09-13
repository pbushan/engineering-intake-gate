import ArrowBackRounded from '@mui/icons-material/ArrowBackRounded';
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
  Grid,
  Link,
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
import ExpandMoreRounded from '@mui/icons-material/ExpandMoreRounded';
import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { RunDetail } from '../api/contracts';
import {
  actorLabel,
  AzureDevOpsLink,
  DecisionChip,
  formatCost,
  formatDateTime,
  formatDuration,
  formatInteger,
  invocationLabel,
  Metric,
  RunStatusChip,
  SafeErrors,
  UsageCost,
} from '../operations/presentation';

export function RunDetailPage() {
  const { runId = '' } = useParams();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const viewKey = `${runId}|${refreshKey}`;
  const [result, setResult] = useState<{ key: string; detail: RunDetail | null; error: ApiError | null }>({ key: '', detail: null, error: null });
  const current = result.key === viewKey ? result : { key: viewKey, detail: null, error: null };
  const { detail, error } = current;

  useEffect(() => {
    const controller = new AbortController();
    void apiClient.getRun(runId, controller.signal).then((response) => {
      setResult({ key: viewKey, detail: response, error: null });
      requestAnimationFrame(() => headingRef.current?.focus());
    }).catch((caught: unknown) => {
      if (!(caught instanceof DOMException && caught.name === 'AbortError')) setResult({ key: viewKey, detail: null, error: asApiError(caught) });
    });
    return () => controller.abort();
  }, [runId, viewKey]);

  if (error) return <Stack spacing={2}><Button component={RouterLink} to="/runs" startIcon={<ArrowBackRounded />} sx={{ alignSelf: 'flex-start' }}>Back to runs</Button><Alert severity="error"><AlertTitle>Run could not be loaded</AlertTitle>{error.message}<Box sx={{ mt: 1 }}><Button color="inherit" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Try again</Button></Box></Alert></Stack>;
  if (!detail) return <RunDetailSkeleton />;

  const { summary } = detail;
  return (
    <Stack spacing={3}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}>
        <Box>
          <Button component={RouterLink} to="/runs" startIcon={<ArrowBackRounded />} sx={{ mb: 1, ml: -1 }}>Back to runs</Button>
          <Typography component="h1" variant="h1" ref={headingRef} tabIndex={-1}>Run {summary.runId.slice(0, 8)}</Typography>
          <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 1 }}><RunStatusChip status={summary.status} /><Typography color="text.secondary">{invocationLabel(summary.invocationType)} · {summary.effectiveMode === 'dryRun' ? 'Dry Run' : summary.effectiveMode}</Typography></Stack>
        </Box>
        <Button variant="outlined" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Refresh</Button>
      </Stack>

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2" gutterBottom>Run metadata</Typography>
          <Grid container spacing={2}>
            <Metadata label="Started" value={formatDateTime(summary.startedAtUtc)} />
            <Metadata label="Completed" value={formatDateTime(summary.completedAtUtc)} />
            <Metadata label="Duration" value={formatDuration(summary.durationMilliseconds)} />
            <Metadata label="Triggered by" value={actorLabel(summary.triggeredBy)} />
            <Metadata label="Profile" value={summary.profileId} />
            <Metadata label="Mode" value={summary.effectiveMode === 'dryRun' ? 'Controlled Dry Run' : summary.effectiveMode} />
          </Grid>
        </CardContent>
      </Card>

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2" gutterBottom>Outcome summary</Typography>
          <Grid container spacing={2.5}>
            <Metric label="Evaluated" value={formatInteger(summary.ticketsEvaluated)} />
            <Metric label="Engineering Ready" value={formatInteger(summary.engineeringReadyCount)} detail="Enough information to begin investigation" />
            <Metric label="Intake Incomplete" value={formatInteger(summary.intakeIncompleteCount)} detail="Required information is missing or insufficient" />
            <Metric label="Error" value={formatInteger(summary.errorCount)} />
            <Metric label="Not Eligible" value={formatInteger(summary.notEligibleCount)} />
            <Metric label="Duplicate updates suppressed" value={formatInteger(summary.duplicateUpdatesSuppressedCount)} />
            <Metric label="Actual ADO changes" value={formatInteger(summary.azureDevOpsMutationCount)} />
            <Metric label="Estimated AI cost" value={formatCost(summary.estimatedCost)} />
          </Grid>
          <Box sx={{ mt: 3 }}><UsageCost usage={summary.tokenUsage} cost={summary.estimatedCost} /></Box>
        </CardContent>
      </Card>

      <SafeErrors errors={summary.errors} />

      <Card variant="outlined">
        <CardContent sx={{ pb: 1 }}><Typography component="h2" variant="h2">Evaluations</Typography></CardContent>
        {detail.items.length > 0 ? (
          <TableContainer>
            <Table aria-label="Run evaluations">
              <TableHead><TableRow><TableCell>Work item</TableCell><TableCell>Outcome</TableCell><TableCell sx={{ display: { xs: 'none', md: 'table-cell' } }}>Eligibility</TableCell><TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>Update state</TableCell><TableCell sx={{ display: { xs: 'none', sm: 'table-cell' } }}>Usage / cost</TableCell><TableCell sx={{ display: { xs: 'none', xl: 'table-cell' } }}>Azure DevOps</TableCell></TableRow></TableHead>
              <TableBody>
                {detail.items.map((item) => (
                  <TableRow key={item.evaluationId} hover>
                    <TableCell><Link component={RouterLink} to={`/runs/${summary.runId}/items/${encodeURIComponent(item.evaluationId)}`} aria-label={`Open evaluation for work item ${item.workItemId}`}>{item.workItemId}</Link></TableCell>
                    <TableCell><DecisionChip decision={item.decision} /></TableCell>
                    <TableCell sx={{ display: { xs: 'none', md: 'table-cell' }, textTransform: 'capitalize' }}>{item.eligibility.replace(/([A-Z])/g, ' $1')}</TableCell>
                    <TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>{item.updateSuppressed ? 'Duplicate Update Suppressed' : 'No suppression reported'}</TableCell>
                    <TableCell sx={{ display: { xs: 'none', sm: 'table-cell' } }}>{item.tokenUsage ? `${formatInteger(item.tokenUsage.totalTokens)} tokens` : '—'}<Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{formatCost(item.estimatedCost)}</Typography></TableCell>
                    <TableCell sx={{ display: { xs: 'none', xl: 'table-cell' } }}><AzureDevOpsLink href={item.azureDevOpsUrl} workItemId={item.workItemId} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        ) : <Box sx={{ p: 4, textAlign: 'center' }}><Typography color="text.secondary">This run has no persisted evaluation rows.</Typography></Box>}
      </Card>

      <Accordion disableGutters>
        <AccordionSummary expandIcon={<ExpandMoreRounded />}><Typography component="h2" variant="h3">Technical generation details</Typography></AccordionSummary>
        <AccordionDetails><Grid container spacing={2}><Metadata label="Run ID" value={summary.runId} /><Metadata label="Configuration generation" value={summary.configurationGenerationId === null ? 'Historical generation unavailable' : String(summary.configurationGenerationId)} /><Metadata label="Profile version" value={detail.profileVersion ?? 'Historical value unavailable'} /><Metadata label="Saved query ID" value={detail.savedQueryId ?? 'Historical value unavailable'} /><Metadata label="Policy version" value={detail.policyVersion || 'Historical value unavailable'} /></Grid></AccordionDetails>
      </Accordion>
    </Stack>
  );
}

function Metadata({ label, value }: { label: string; value: string }) {
  return <Grid size={{ xs: 12, sm: 6, lg: 4 }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography sx={{ overflowWrap: 'anywhere' }}>{value}</Typography></Grid>;
}

function RunDetailSkeleton() {
  return <Stack spacing={2} aria-busy="true"><Typography role="status">Loading run detail…</Typography><Skeleton variant="rounded" height={110} /><Skeleton variant="rounded" height={210} /><Skeleton variant="rounded" height={260} /></Stack>;
}
