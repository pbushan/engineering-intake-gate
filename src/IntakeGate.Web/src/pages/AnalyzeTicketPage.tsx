import SearchRounded from '@mui/icons-material/SearchRounded';
import { Alert, AlertTitle, Box, Button, Card, CardContent, CircularProgress, Link, Stack, TextField, Typography } from '@mui/material';
import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { AnalyzeWorkItemRequest, AnalyzeWorkItemResponse } from '../api/contracts';

const parseIdentity = (value: string): AnalyzeWorkItemRequest | null => {
  const normalized = value.trim();
  if (/^[0-9]+$/.test(normalized)) {
    const id = Number(normalized);
    return Number.isSafeInteger(id) && id > 0 && id <= 2_147_483_647
      ? { workItemId: id, workItemUrl: null }
      : null;
  }
  try {
    const url = new URL(normalized);
    return /^https?:$/.test(url.protocol) && /\/_workitems\/edit\/[1-9][0-9]*\/?$/i.test(url.pathname)
      ? { workItemId: null, workItemUrl: normalized }
      : null;
  } catch {
    return null;
  }
};

export function AnalyzeTicketPage() {
  const navigate = useNavigate();
  const statusRef = useRef<HTMLDivElement>(null);
  const [identity, setIdentity] = useState('');
  const [validation, setValidation] = useState<string | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [result, setResult] = useState<AnalyzeWorkItemResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    const request = parseIdentity(identity);
    if (!request) {
      setValidation('Enter a positive work-item ID or a full supported Azure DevOps work-item URL.');
      setError(null);
      return;
    }
    setValidation(null);
    setError(null);
    setResult(null);
    setSubmitting(true);
    try {
      const response = await apiClient.analyzeWorkItem(request);
      if (response.decision === 'notEligible') {
        setResult(response);
        requestAnimationFrame(() => statusRef.current?.focus());
      } else {
        void navigate(`/runs/${response.runId}`);
      }
    } catch (caught) {
      setError(asApiError(caught));
      requestAnimationFrame(() => statusRef.current?.focus());
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Stack spacing={3.5}>
      <Box>
        <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Governed manual analysis</Typography>
        <Typography component="h1" variant="h1">Analyze Ticket</Typography>
        <Typography color="text.secondary" sx={{ mt: 1, maxWidth: 760 }}>
          Analyze one work item using the active Controlled Dry Run configuration. The configured saved query remains authoritative.
        </Typography>
      </Box>

      <Alert severity="info">
        The work item must be inside the configured intake query and satisfy the current eligibility boundary. There is no out-of-query override.
      </Alert>

      <Card sx={{ maxWidth: 760 }}>
        <CardContent>
          <Stack component="form" spacing={2.5} onSubmit={(event) => void submit(event)} noValidate>
            <TextField
              autoFocus
              fullWidth
              label="Azure DevOps work-item ID or URL"
              value={identity}
              onChange={(event) => { setIdentity(event.target.value); setValidation(null); }}
              error={Boolean(validation)}
              helperText={validation ?? 'Examples: 12345 or the full Azure DevOps work-item edit URL.'}
              disabled={submitting}
            />
            <Button type="submit" variant="contained" startIcon={submitting ? <CircularProgress color="inherit" size={18} /> : <SearchRounded />} disabled={submitting} sx={{ alignSelf: 'flex-start' }}>
              {submitting ? 'Analyzing ticket…' : 'Analyze Ticket'}
            </Button>
            {submitting ? <Typography role="status" aria-live="polite" color="text.secondary">Running the governed analysis. Keep this page open while the synchronous request completes.</Typography> : null}
          </Stack>
        </CardContent>
      </Card>

      {result?.decision === 'notEligible' ? (
        <Alert severity="warning" ref={statusRef} tabIndex={-1} role="status" aria-live="polite">
          <AlertTitle>Not Eligible</AlertTitle>
          This work item is outside the configured intake query or does not satisfy the current eligibility boundary.
          <Box sx={{ mt: 1 }}><Link component={RouterLink} to={`/runs/${result.runId}`}>View the persisted run record</Link></Box>
        </Alert>
      ) : null}

      {error ? (
        <Alert severity={error.kind === 'conflict' ? 'warning' : 'error'} ref={statusRef} tabIndex={-1} role="alert">
          <AlertTitle>{error.code === 'ActiveRunInProgress' ? 'A run is already active for this profile' : 'Analysis could not be completed'}</AlertTitle>
          {error.code === 'ActiveRunInProgress' ? 'Wait for the active run to finish, then submit again.' : error.message}
        </Alert>
      ) : null}
    </Stack>
  );
}
