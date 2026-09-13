import PlayArrowRounded from '@mui/icons-material/PlayArrowRounded';
import SearchRounded from '@mui/icons-material/SearchRounded';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Grid,
  LinearProgress,
  Link,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import { useCallback, useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { HomeSummary, HomeWindowDays, RunSummary } from '../api/contracts';
import { useAuth } from '../auth/AuthContext';
import { PageError } from '../components/PageError';
import { formatDateTime } from '../operations/presentation';

const windowOptions: HomeWindowDays[] = [7, 30, 90];
const words = (value: string): string => value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (letter) => letter.toUpperCase());

function MetricCard({ title, value, detail, children }: { title: string; value: string; detail: string; children?: React.ReactNode }) {
  return (
    <Card variant="outlined" sx={{ height: '100%' }}>
      <CardContent>
        <Typography component="h3" variant="overline" color="text.secondary">{title}</Typography>
        <Typography sx={{ mt: 0.5, fontSize: { xs: '1.8rem', md: '2.15rem' }, lineHeight: 1.1, fontWeight: 800 }}>{value}</Typography>
        {children}
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{detail}</Typography>
      </CardContent>
    </Card>
  );
}

function money(summary: HomeSummary): string {
  const amount = summary.estimatedAiCost.amount;
  const currency = summary.estimatedAiCost.currency;
  if (amount === null || currency === null) return summary.evaluatedCount === 0 ? '0' : '—';
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: 4 }).format(Number(amount));
  } catch {
    return `${currency} ${Number(amount).toFixed(4)}`;
  }
}

function runCost(run: RunSummary): string {
  if (!run.estimatedCost) return '—';
  return `${run.estimatedCost.currency} ${Number(run.estimatedCost.amount).toFixed(4)}`;
}

export function HomePage() {
  const { user } = useAuth();
  const [windowDays, setWindowDays] = useState<HomeWindowDays>(30);
  const [summary, setSummary] = useState<HomeSummary | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback((signal?: AbortSignal) =>
    apiClient.getHomeSummary(windowDays, signal).then((result) => {
      setSummary(result);
      setError(null);
    }).catch((caught: unknown) => {
      if (!(caught instanceof DOMException && caught.name === 'AbortError')) {
        setSummary(null);
        setError(asApiError(caught));
      }
    }).finally(() => {
      if (!signal?.aborted) setLoading(false);
    }), [windowDays]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  if (!user) return null;
  const rate = summary?.engineeringReadyRate.percentage;

  return (
    <Stack spacing={3.5}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-start' } }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Operational overview</Typography>
            <Chip label="Controlled Dry Run" color="success" size="small" />
          </Stack>
          <Typography component="h1" variant="h1">Engineering Intake Gate</Typography>
          <Typography color="text.secondary" sx={{ mt: 1, maxWidth: 760 }}>
            Intake readiness and operational activity from persisted backend state. Production remains unavailable.
          </Typography>
        </Box>
        <Box>
          <Typography id="summary-window-label" variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.75 }}>Summary window</Typography>
          <ToggleButtonGroup
            exclusive
            value={windowDays}
            aria-labelledby="summary-window-label"
            onChange={(_event, value: HomeWindowDays | null) => { if (value !== null) { setLoading(true); setWindowDays(value); } }}
            size="small"
          >
            {windowOptions.map((days) => <ToggleButton key={days} value={days} aria-label={`Last ${days} days`}>{days} days</ToggleButton>)}
          </ToggleButtonGroup>
        </Box>
      </Stack>

      {loading ? (
        <Card aria-busy="true"><CardContent>
          <Typography role="status" aria-live="polite">Loading {windowDays}-day operational summary…</Typography>
          <Grid container spacing={2} sx={{ mt: 0.5 }}>{Array.from({ length: 5 }, (_, index) => <Grid key={index} size={{ xs: 12, sm: 6, lg: 2.4 }}><Skeleton height={120} /></Grid>)}</Grid>
        </CardContent></Card>
      ) : null}
      {error && !loading ? <PageError error={error} retry={() => { setLoading(true); return load(); }} /> : null}

      {summary && !loading ? <>
        <Box component="section" aria-labelledby="primary-metrics-heading">
          <Typography id="primary-metrics-heading" component="h2" variant="h2" gutterBottom>Last {summary.windowDays} days</Typography>
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, sm: 6, lg: 2.4 }}>
              <MetricCard title="Engineering-Ready Rate" value={rate === null ? '—' : `${Number(rate).toFixed(1)}%`} detail={rate === null ? 'No Engineering Ready or Intake Incomplete assessments in this window.' : `${summary.engineeringReadyRate.numerator} Engineering Ready of ${summary.engineeringReadyRate.denominator} Engineering Ready + Intake Incomplete assessments.`}>
                <LinearProgress aria-label={rate === null ? 'Engineering-Ready Rate unavailable' : `Engineering-Ready Rate ${Number(rate).toFixed(1)} percent`} variant="determinate" value={rate === null ? 0 : Number(rate)} sx={{ mt: 1.5, height: 8, borderRadius: 4 }} />
              </MetricCard>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2.4 }}><MetricCard title="Tickets Evaluated" value={String(summary.evaluatedCount)} detail="Engineering Ready, Intake Incomplete, and technical error assessments." /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2.4 }}><MetricCard title="Engineering Ready" value={String(summary.engineeringReadyCount)} detail="Enough intake context to begin investigation." /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2.4 }}><MetricCard title="Intake Incomplete" value={String(summary.intakeIncompleteCount)} detail="More relevant intake context is needed." /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2.4 }}><MetricCard title="Errors" value={String(summary.errorCount)} detail="Technical/system failures; never Intake Incomplete." /></Grid>
          </Grid>
        </Box>

        {summary.evaluatedCount === 0 ? <Alert severity="info">No evaluated tickets in the last {summary.windowDays} days.</Alert> : null}

        <Box component="section" aria-labelledby="operational-metrics-heading">
          <Typography id="operational-metrics-heading" component="h2" variant="h2" gutterBottom>Operational metrics</Typography>
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, md: 4 }}><MetricCard title="Duplicate Updates Suppressed" value={String(summary.duplicateUpdatesSuppressedCount)} detail="Explicit persisted materially-unchanged suppressions only." /></Grid>
            <Grid size={{ xs: 12, md: 4 }}><MetricCard title="Estimated AI Cost" value={money(summary)} detail={summary.estimatedAiCost.complete ? 'Sum of persisted historical estimates; no repricing.' : `${summary.estimatedAiCost.evaluationsWithoutEstimate} evaluated ticket(s) have no persisted estimate.`} /></Grid>
            <Grid size={{ xs: 12, md: 4 }}><MetricCard title="Not Eligible" value={String(summary.notEligibleCount)} detail="Governance/eligibility outcomes, excluded from the readiness rate." /></Grid>
          </Grid>
        </Box>

        <Box component="section" aria-labelledby="attention-heading">
          <Typography id="attention-heading" component="h2" variant="h2" gutterBottom>Health and attention</Typography>
          {summary.healthWarnings.length === 0 ? (
            <Alert severity="success"><AlertTitle>No health warnings</AlertTitle>Persisted supportability state does not currently require operator attention.</Alert>
          ) : (
            <Stack spacing={1.5}>{summary.healthWarnings.map((warning) => (
              <Alert key={warning.code} severity="warning">
                <AlertTitle>{warning.title}</AlertTitle>{warning.message}{' '}
                <Link component={RouterLink} to={warning.detailUrl}>Review details</Link>
              </Alert>
            ))}</Stack>
          )}
        </Box>

        <Box component="section" aria-labelledby="recent-runs-heading">
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: 1 }}>
            <Typography id="recent-runs-heading" component="h2" variant="h2">Recent Runs</Typography>
            <Button component={RouterLink} to="/runs">View all runs</Button>
          </Stack>
          {summary.recentRuns.length === 0 ? (
            <Card variant="outlined"><CardContent><Typography>No runs started in the selected window.</Typography></CardContent></Card>
          ) : (
            <TableContainer component={Card} variant="outlined">
              <Table aria-label={`Recent runs in the last ${summary.windowDays} days`}>
                <TableHead><TableRow><TableCell>Run</TableCell><TableCell>Started</TableCell><TableCell>Invocation</TableCell><TableCell>Status</TableCell><TableCell align="right">Evaluated</TableCell><TableCell align="right">Ready / Incomplete</TableCell><TableCell align="right">Estimated cost</TableCell></TableRow></TableHead>
                <TableBody>{summary.recentRuns.map((run) => <TableRow key={run.runId}>
                  <TableCell><Link component={RouterLink} to={`/runs/${run.runId}`} aria-label={`Open run ${run.runId}`}>{run.runId.slice(0, 8)}</Link></TableCell>
                  <TableCell>{formatDateTime(run.startedAtUtc)}</TableCell>
                  <TableCell>{words(run.invocationType)}</TableCell>
                  <TableCell>{words(run.status)}</TableCell>
                  <TableCell align="right">{run.ticketsEvaluated}</TableCell>
                  <TableCell align="right">{run.engineeringReadyCount} / {run.intakeIncompleteCount}</TableCell>
                  <TableCell align="right">{runCost(run)}</TableCell>
                </TableRow>)}</TableBody>
              </Table>
            </TableContainer>
          )}
        </Box>

        {user.role === 'admin' ? <Card variant="outlined"><CardContent>
          <Typography component="h2" variant="h2">Quick actions</Typography>
          <Typography color="text.secondary" sx={{ mt: 0.75 }}>Open the existing authorized Controlled Dry Run workflows.</Typography>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ mt: 2 }}>
            <Button component={RouterLink} to="/analyze" variant="contained" startIcon={<SearchRounded />}>Analyze Ticket</Button>
            <Button component={RouterLink} to="/runs" variant="outlined" startIcon={<PlayArrowRounded />}>Run Profile Now</Button>
          </Stack>
        </CardContent></Card> : null}
      </> : null}
    </Stack>
  );
}
