import PlayArrowRounded from '@mui/icons-material/PlayArrowRounded';
import RefreshRounded from '@mui/icons-material/RefreshRounded';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  CircularProgress,
  FormControl,
  Grid,
  InputLabel,
  Link,
  MenuItem,
  Pagination,
  Select,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useNavigate, useSearchParams } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { OperationalRunStatus, RunHistoryPage, RunTriggerType } from '../api/contracts';
import { useAuth } from '../auth/AuthContext';
import {
  actorLabel,
  costCoverage,
  formatCost,
  formatDateTime,
  formatInteger,
  invocationLabel,
  RunStatusChip,
} from '../operations/presentation';

const pageSize = 20;
const invocationValues: RunTriggerType[] = ['scheduled', 'manualIncremental', 'manualWorkItem'];
const statusValues: OperationalRunStatus[] = ['running', 'completed', 'completedWithErrors', 'error'];
const positivePage = (value: string | null) => Math.max(1, Number.parseInt(value ?? '1', 10) || 1);

const dateBoundary = (value: string, end: boolean): string | undefined => {
  if (!value) return undefined;
  const date = new Date(`${value}T${end ? '23:59:59.999' : '00:00:00.000'}Z`);
  return Number.isNaN(date.valueOf()) ? undefined : date.toISOString();
};

export function RunsPage() {
  const { user } = useAuth();
  const admin = user?.role === 'admin';
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = positivePage(searchParams.get('page'));
  const appliedFrom = searchParams.get('from') ?? '';
  const appliedTo = searchParams.get('to') ?? '';
  const invocationParam = searchParams.get('invocation');
  const statusParam = searchParams.get('status');
  const appliedInvocation = invocationValues.includes(invocationParam as RunTriggerType) ? invocationParam as RunTriggerType : '';
  const appliedStatus = statusValues.includes(statusParam as OperationalRunStatus) ? statusParam as OperationalRunStatus : '';
  const appliedWorkItem = searchParams.get('workItemId') ?? '';
  const [filters, setFilters] = useState({ from: appliedFrom, to: appliedTo, invocation: appliedInvocation, status: appliedStatus, workItemId: appliedWorkItem });
  const [refreshKey, setRefreshKey] = useState(0);
  const viewKey = `${page}|${appliedFrom}|${appliedTo}|${appliedInvocation}|${appliedStatus}|${appliedWorkItem}|${refreshKey}`;
  const [result, setResult] = useState<{ key: string; data: RunHistoryPage | null; error: ApiError | null }>({ key: '', data: null, error: null });
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<ApiError | null>(null);
  const runErrorRef = useRef<HTMLDivElement>(null);
  const current = result.key === viewKey ? result : { key: viewKey, data: null, error: null };
  const { data, error } = current;
  const appliedSearch = searchParams.toString();

  useEffect(() => {
    const controller = new AbortController();
    const startedFromUtc = dateBoundary(appliedFrom, false);
    const startedToUtc = dateBoundary(appliedTo, true);
    void apiClient.listRuns({
      page,
      pageSize,
      ...(startedFromUtc ? { startedFromUtc } : {}),
      ...(startedToUtc ? { startedToUtc } : {}),
      ...(appliedInvocation ? { invocationType: appliedInvocation } : {}),
      ...(appliedStatus ? { status: appliedStatus } : {}),
      ...(appliedWorkItem.trim() ? { workItemId: appliedWorkItem.trim() } : {}),
    }, controller.signal).then((response) => {
      const totalPages = Number(response.totalPages);
      if (totalPages > 0 && page > totalPages) {
        const next = new URLSearchParams(appliedSearch);
        next.set('page', String(totalPages));
        void setSearchParams(next, { replace: true });
        return;
      }
      setResult({ key: viewKey, data: response, error: null });
    }).catch((caught: unknown) => {
      if (!(caught instanceof DOMException && caught.name === 'AbortError')) setResult({ key: viewKey, data: null, error: asApiError(caught) });
    });
    return () => controller.abort();
  }, [appliedFrom, appliedInvocation, appliedSearch, appliedStatus, appliedTo, appliedWorkItem, page, setSearchParams, viewKey]);

  const applyFilters = () => {
    const next = new URLSearchParams();
    if (filters.from) next.set('from', filters.from);
    if (filters.to) next.set('to', filters.to);
    if (filters.invocation) next.set('invocation', filters.invocation);
    if (filters.status) next.set('status', filters.status);
    if (filters.workItemId.trim()) next.set('workItemId', filters.workItemId.trim());
    next.set('page', '1');
    void setSearchParams(next);
  };

  const clearFilters = () => {
    setFilters({ from: '', to: '', invocation: '', status: '', workItemId: '' });
    void setSearchParams({ page: '1' });
  };

  const runNow = async () => {
    if (running) return;
    setRunning(true);
    setRunError(null);
    try {
      const result = await apiClient.runProfileNow();
      void navigate(`/runs/${result.runId}`);
    } catch (caught) {
      setRunError(asApiError(caught));
      requestAnimationFrame(() => runErrorRef.current?.focus());
    } finally {
      setRunning(false);
    }
  };

  const filtered = Boolean(appliedFrom || appliedTo || appliedInvocation || appliedStatus || appliedWorkItem);

  return (
    <Stack spacing={3}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-start' } }}>
        <Box>
          <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Operational history</Typography>
          <Typography component="h1" variant="h1">Runs</Typography>
          <Typography color="text.secondary" sx={{ mt: 1 }}>Backend-persisted run history, newest first.</Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button variant="outlined" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Refresh</Button>
          {admin ? (
            <Button variant="contained" startIcon={running ? <CircularProgress size={18} color="inherit" /> : <PlayArrowRounded />} disabled={running} onClick={() => void runNow()}>
              {running ? 'Running profile…' : 'Run Profile Now'}
            </Button>
          ) : null}
        </Stack>
      </Stack>

      {admin ? <Alert severity="info">Runs the configured intake query using the active Dry Run configuration. No Azure DevOps modifications are made.</Alert> : null}
      {runError ? (
        <Alert severity={runError.kind === 'conflict' ? 'warning' : 'error'} ref={runErrorRef} tabIndex={-1} role="alert">
          <AlertTitle>{runError.code === 'ActiveRunInProgress' ? 'A run is already active for this profile' : 'The run could not start'}</AlertTitle>
          {runError.code === 'ActiveRunInProgress' ? 'No additional run was queued.' : runError.message}
        </Alert>
      ) : null}

      <Card variant="outlined">
        <CardContent>
          <Typography component="h2" variant="h2" gutterBottom>Filter runs</Typography>
          <Grid container spacing={2} sx={{ alignItems: 'end' }} component="form" onSubmit={(event) => { event.preventDefault(); applyFilters(); }}>
            <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth type="date" label="Started from" value={filters.from} onChange={(event) => setFilters({ ...filters, from: event.target.value })} slotProps={{ inputLabel: { shrink: true } }} /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth type="date" label="Started to" value={filters.to} onChange={(event) => setFilters({ ...filters, to: event.target.value })} slotProps={{ inputLabel: { shrink: true } }} /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2 }}>
              <FormControl fullWidth><InputLabel id="invocation-filter-label">Invocation</InputLabel><Select labelId="invocation-filter-label" label="Invocation" value={filters.invocation} onChange={(event) => setFilters({ ...filters, invocation: event.target.value })}><MenuItem value="">All</MenuItem>{invocationValues.map((value) => <MenuItem key={value} value={value}>{invocationLabel(value)}</MenuItem>)}</Select></FormControl>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2 }}>
              <FormControl fullWidth><InputLabel id="status-filter-label">Run status</InputLabel><Select labelId="status-filter-label" label="Run status" value={filters.status} onChange={(event) => setFilters({ ...filters, status: event.target.value })}><MenuItem value="">All</MenuItem><MenuItem value="running">Running</MenuItem><MenuItem value="completed">Completed</MenuItem><MenuItem value="completedWithErrors">Completed with errors</MenuItem><MenuItem value="error">Execution error</MenuItem></Select></FormControl>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth label="Work-item ID" value={filters.workItemId} onChange={(event) => setFilters({ ...filters, workItemId: event.target.value })} /></Grid>
            <Grid size={{ xs: 12, lg: 2 }}><Stack direction="row" spacing={1}><Button type="submit" variant="contained">Apply</Button><Button type="button" onClick={clearFilters}>Clear</Button></Stack></Grid>
          </Grid>
        </CardContent>
      </Card>

      {error ? <Alert severity="error"><AlertTitle>Runs could not be loaded</AlertTitle>{error.message}<Box sx={{ mt: 1 }}><Button color="inherit" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((key) => key + 1)}>Try again</Button></Box></Alert> : null}

      {!data && !error ? <RunsSkeleton /> : null}
      {data ? (
        <Card variant="outlined">
          <TableContainer>
            <Table aria-label="Run history">
              <TableHead><TableRow><TableCell>Run</TableCell><TableCell>Started</TableCell><TableCell>Invocation</TableCell><TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>Triggered by</TableCell><TableCell>Status</TableCell><TableCell>Outcomes</TableCell><TableCell sx={{ display: { xs: 'none', xl: 'table-cell' } }}>Suppressed</TableCell><TableCell sx={{ display: { xs: 'none', md: 'table-cell' } }}>Estimated AI cost</TableCell></TableRow></TableHead>
              <TableBody>
                {data.items.map((run) => (
                  <TableRow key={run.runId} hover>
                    <TableCell><Link component={RouterLink} to={`/runs/${run.runId}`} aria-label={`Open run ${run.runId}`}>{run.runId.slice(0, 8)}</Link></TableCell>
                    <TableCell>{formatDateTime(run.startedAtUtc)}</TableCell>
                    <TableCell>{invocationLabel(run.invocationType)}</TableCell>
                    <TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>{actorLabel(run.triggeredBy)}</TableCell>
                    <TableCell><RunStatusChip status={run.status} /></TableCell>
                    <TableCell><Typography variant="body2">{formatInteger(run.ticketsEvaluated)} evaluated</Typography><Typography variant="caption" color="text.secondary">{formatInteger(run.engineeringReadyCount)} ready · {formatInteger(run.intakeIncompleteCount)} incomplete · {formatInteger(run.errorCount)} error{Number(run.notEligibleCount) ? ` · ${formatInteger(run.notEligibleCount)} not eligible` : ''}</Typography></TableCell>
                    <TableCell sx={{ display: { xs: 'none', xl: 'table-cell' } }}>{formatInteger(run.duplicateUpdatesSuppressedCount)}</TableCell>
                    <TableCell sx={{ display: { xs: 'none', md: 'table-cell' } }}>{formatCost(run.estimatedCost)}{costCoverage(run.estimatedCost) ? <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{costCoverage(run.estimatedCost)}</Typography> : null}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          {data.items.length === 0 ? <Box sx={{ p: 4, textAlign: 'center' }}><Typography component="h2" variant="h3">{filtered ? 'No runs match these filters.' : 'No runs yet.'}</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>{filtered ? 'Clear or change the filters to see other persisted runs.' : 'Run history will appear here after the first execution.'}</Typography></Box> : null}
          {Number(data.totalPages) > 1 ? <Stack sx={{ p: 2, alignItems: 'center' }}><Pagination page={Number(data.page)} count={Number(data.totalPages)} onChange={(_event, nextPage) => { const next = new URLSearchParams(searchParams); next.set('page', String(nextPage)); void setSearchParams(next); }} aria-label="Run history pages" /></Stack> : null}
        </Card>
      ) : null}
    </Stack>
  );
}

function RunsSkeleton() {
  return <Card aria-busy="true" aria-label="Loading run history"><CardContent><Typography role="status" sx={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden' }}>Loading run history…</Typography>{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} height={54} />)}</CardContent></Card>;
}
