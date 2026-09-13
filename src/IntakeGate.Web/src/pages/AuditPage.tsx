import ExpandLessRounded from '@mui/icons-material/ExpandLessRounded';
import ExpandMoreRounded from '@mui/icons-material/ExpandMoreRounded';
import RefreshRounded from '@mui/icons-material/RefreshRounded';
import {
  Alert, AlertTitle, Box, Button, Card, CardContent, Chip, Collapse, Grid, IconButton,
  Pagination, Skeleton, Stack, Table, TableBody, TableCell, TableContainer, TableHead,
  TableRow, TextField, Typography,
} from '@mui/material';
import { Fragment, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { ControlPlaneAuditPage } from '../api/contracts';
import { formatDateTime } from '../operations/presentation';

const pageSize = 20;
const positivePage = (value: string | null) => Math.max(1, Number.parseInt(value ?? '1', 10) || 1);
const dateBoundary = (value: string, end: boolean): string | undefined => value ? new Date(`${value}T${end ? '23:59:59.999' : '00:00:00.000'}Z`).toISOString() : undefined;

export function AuditPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const page = positivePage(searchParams.get('page'));
  const appliedFrom = searchParams.get('from') ?? '';
  const appliedTo = searchParams.get('to') ?? '';
  const appliedActor = searchParams.get('actor') ?? '';
  const appliedOperation = searchParams.get('operation') ?? '';
  const appliedCategory = searchParams.get('category') ?? '';
  const appliedTarget = searchParams.get('target') ?? '';
  const [filters, setFilters] = useState({ from: appliedFrom, to: appliedTo, actor: appliedActor, operation: appliedOperation, category: appliedCategory, target: appliedTarget });
  const [refreshKey, setRefreshKey] = useState(0);
  const [data, setData] = useState<ControlPlaneAuditPage | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const occurredFromUtc = dateBoundary(appliedFrom, false);
    const occurredToUtc = dateBoundary(appliedTo, true);
    void apiClient.listAudit({ page, pageSize,
      ...(occurredFromUtc ? { occurredFromUtc } : {}),
      ...(occurredToUtc ? { occurredToUtc } : {}),
      ...(appliedActor ? { actor: appliedActor } : {}), ...(appliedOperation ? { operation: appliedOperation } : {}),
      ...(appliedCategory ? { targetCategory: appliedCategory } : {}), ...(appliedTarget ? { targetId: appliedTarget } : {}),
    }, controller.signal).then((result) => { setData(result); setError(null); }).catch((caught: unknown) => {
      if (!(caught instanceof DOMException && caught.name === 'AbortError')) setError(asApiError(caught));
    });
    return () => controller.abort();
  }, [appliedActor, appliedCategory, appliedFrom, appliedOperation, appliedTarget, appliedTo, page, refreshKey]);

  const apply = () => {
    const next = new URLSearchParams({ page: '1' });
    Object.entries(filters).forEach(([key, value]) => { if (value.trim()) next.set(key, value.trim()); });
    void setSearchParams(next);
  };
  const clear = () => { const empty = { from: '', to: '', actor: '', operation: '', category: '', target: '' }; setFilters(empty); void setSearchParams({ page: '1' }); };

  return <Stack spacing={3}>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}>
      <Box><Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Supportability</Typography><Typography component="h1" variant="h1">Audit</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>Safe control-plane, configuration, user, and execution-request history. Newest first.</Typography></Box>
      <Button variant="outlined" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((value) => value + 1)}>Refresh</Button>
    </Stack>

    <Card variant="outlined"><CardContent><Typography component="h2" variant="h2" gutterBottom>Filter audit history</Typography>
      <Grid container spacing={2} component="form" onSubmit={(event) => { event.preventDefault(); apply(); }}>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth type="date" label="Occurred from" value={filters.from} onChange={(event) => setFilters({ ...filters, from: event.target.value })} slotProps={{ inputLabel: { shrink: true } }} /></Grid>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth type="date" label="Occurred to" value={filters.to} onChange={(event) => setFilters({ ...filters, to: event.target.value })} slotProps={{ inputLabel: { shrink: true } }} /></Grid>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth label="Actor username" value={filters.actor} onChange={(event) => setFilters({ ...filters, actor: event.target.value })} /></Grid>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth label="Operation" value={filters.operation} onChange={(event) => setFilters({ ...filters, operation: event.target.value })} /></Grid>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth label="Target category" value={filters.category} onChange={(event) => setFilters({ ...filters, category: event.target.value })} /></Grid>
        <Grid size={{ xs: 12, sm: 6, lg: 2 }}><TextField fullWidth label="Target / profile" value={filters.target} onChange={(event) => setFilters({ ...filters, target: event.target.value })} /></Grid>
        <Grid size={{ xs: 12 }}><Stack direction="row" spacing={1}><Button type="submit" variant="contained">Apply filters</Button><Button type="button" onClick={clear}>Clear</Button></Stack></Grid>
      </Grid>
    </CardContent></Card>

    {error ? <Alert severity="error"><AlertTitle>Audit history could not be loaded</AlertTitle>{error.message}</Alert> : null}
    {!data && !error ? <Card aria-busy="true"><CardContent><Typography role="status">Loading audit history…</Typography>{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} height={48} />)}</CardContent></Card> : null}
    {data ? <Card variant="outlined"><TableContainer><Table aria-label="Control-plane audit history"><TableHead><TableRow><TableCell width={56}>Details</TableCell><TableCell>Time</TableCell><TableCell>Actor</TableCell><TableCell>Operation</TableCell><TableCell>Target</TableCell><TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>Summary</TableCell></TableRow></TableHead><TableBody>
      {data.items.map((item) => <Fragment key={item.id}><TableRow hover><TableCell><IconButton size="small" aria-label={`${expanded === item.id ? 'Collapse' : 'Expand'} audit event ${item.operation}`} aria-expanded={expanded === item.id} onClick={() => setExpanded(expanded === item.id ? null : item.id)}>{expanded === item.id ? <ExpandLessRounded /> : <ExpandMoreRounded />}</IconButton></TableCell><TableCell>{formatDateTime(item.occurredAtUtc)}</TableCell><TableCell>{item.actor.displayName}</TableCell><TableCell>{item.operation}</TableCell><TableCell><Typography variant="body2">{item.targetCategory}</Typography><Typography variant="caption" color="text.secondary">{item.targetId}</Typography></TableCell><TableCell sx={{ display: { xs: 'none', lg: 'table-cell' } }}>{item.changedFields.length ? `${item.changedFields.length} safe field${item.changedFields.length === 1 ? '' : 's'} changed` : 'Recorded event'}</TableCell></TableRow>
        <TableRow><TableCell colSpan={6} sx={{ py: 0, borderBottom: expanded === item.id ? undefined : 0 }}><Collapse in={expanded === item.id} timeout="auto" unmountOnExit><Box sx={{ py: 2, pl: 6 }}><Typography component="h3" variant="h3">Safe event metadata</Typography><Typography variant="body2" sx={{ mt: 1 }}><strong>Actor type:</strong> {item.actor.type}</Typography><Typography variant="body2"><strong>Audit ID:</strong> {item.id}</Typography><Stack direction="row" spacing={1} useFlexGap sx={{ mt: 1.5, flexWrap: 'wrap' }}>{item.changedFields.length ? item.changedFields.map((field) => <Chip key={field} label={field} size="small" variant="outlined" />) : <Typography color="text.secondary">No safe changed-field metadata was persisted.</Typography>}</Stack></Box></Collapse></TableCell></TableRow></Fragment>)}
    </TableBody></Table></TableContainer>
      {data.items.length === 0 ? <Box sx={{ p: 4, textAlign: 'center' }}><Typography component="h2" variant="h3">No audit events match these filters.</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>Historical gaps remain empty; actor or event data is never invented.</Typography></Box> : null}
      {Number(data.totalPages) > 1 ? <Stack sx={{ p: 2, alignItems: 'center' }}><Pagination aria-label="Audit history pages" page={Number(data.page)} count={Number(data.totalPages)} onChange={(_event, nextPage) => { const next = new URLSearchParams(searchParams); next.set('page', String(nextPage)); void setSearchParams(next); }} /></Stack> : null}
    </Card> : null}
  </Stack>;
}
