import CheckCircleOutlineRounded from '@mui/icons-material/CheckCircleOutlineRounded';
import RefreshRounded from '@mui/icons-material/RefreshRounded';
import ScienceOutlined from '@mui/icons-material/ScienceOutlined';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Grid,
  Link,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { SystemHealth } from '../api/contracts';
import { useAuth } from '../auth/AuthContext';
import { formatDateTime } from '../operations/presentation';

type Tone = 'success' | 'warning' | 'error' | 'default' | 'info';

const words = (value: string): string => value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (letter) => letter.toUpperCase());
const connectionLabel = (value: string): string => ({
  verified: 'Verified', credentialMissing: 'Credential missing', authenticationFailed: 'Authentication failed',
  authorizationFailed: 'Authorization failed', rateLimited: 'Rate limited', providerUnavailable: 'Provider unavailable',
  timeout: 'Timeout', notVerified: 'Not yet verified',
})[value] ?? words(value);
const connectionTone = (value: string): Tone => value === 'verified' ? 'success' : value === 'notVerified' || value === 'credentialMissing' ? 'warning' : 'error';

function HealthCard({ title, status, tone, detail, children }: { title: string; status: string; tone: Tone; detail: string; children?: React.ReactNode }) {
  return (
    <Card variant="outlined" sx={{ height: '100%' }}>
      <CardContent><Stack spacing={1.25}>
        <Typography component="h3" variant="h3">{title}</Typography>
        <Chip icon={tone === 'success' ? <CheckCircleOutlineRounded /> : undefined} label={status} color={tone} variant={tone === 'default' ? 'outlined' : 'filled'} sx={{ alignSelf: 'flex-start', fontWeight: 700 }} />
        <Typography variant="body2" color="text.secondary">{detail}</Typography>
        {children}
      </Stack></CardContent>
    </Card>
  );
}

export function SystemHealthPage() {
  const { user } = useAuth();
  const admin = user?.role === 'admin';
  const [health, setHealth] = useState<SystemHealth | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [testing, setTesting] = useState<'ado' | 'ai' | null>(null);
  const [testResult, setTestResult] = useState<{ severity: 'success' | 'error'; title: string; message: string } | null>(null);
  const resultRef = useRef<HTMLDivElement>(null);

  const load = useCallback((signal?: AbortSignal) => apiClient.getSystemHealth(signal).then((result) => {
    setHealth(result); setError(null);
  }).catch((caught: unknown) => {
    if (!(caught instanceof DOMException && caught.name === 'AbortError')) setError(asApiError(caught));
  }), []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load, refreshKey]);

  const testConnection = async (kind: 'ado' | 'ai') => {
    if (testing || !health) return;
    setTesting(kind); setTestResult(null);
    try {
      if (kind === 'ado') await apiClient.testAzureDevOpsConnection();
      else if (health.ai.provider === 'openai' || health.ai.provider === 'anthropic') await apiClient.testAiCredential(health.ai.provider);
      else throw new Error('No supported AI provider is configured.');
      await load();
      setTestResult({ severity: 'success', title: kind === 'ado' ? 'Azure DevOps verified' : 'AI provider verified', message: 'Persisted verification state and timestamp were refreshed.' });
    } catch (caught) {
      const issue = asApiError(caught);
      await load();
      setTestResult({ severity: 'error', title: kind === 'ado' ? 'Azure DevOps test failed' : 'AI provider verification failed', message: issue.message });
    } finally {
      setTesting(null);
      requestAnimationFrame(() => resultRef.current?.focus());
    }
  };

  return (
    <Stack spacing={3.5}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}>
        <Box><Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>Supportability</Typography><Typography component="h1" variant="h1">System Health</Typography><Typography color="text.secondary" sx={{ mt: 1, maxWidth: 760 }}>Backend-owned application, setup, integration, runtime, and scheduler state. Opening this page never contacts external providers.</Typography></Box>
        <Button variant="outlined" startIcon={<RefreshRounded />} onClick={() => setRefreshKey((value) => value + 1)}>Refresh</Button>
      </Stack>

      {error ? <Alert severity="error"><AlertTitle>System health could not be loaded</AlertTitle>{error.message}<Box sx={{ mt: 1 }}><Button color="inherit" onClick={() => setRefreshKey((value) => value + 1)}>Try again</Button></Box></Alert> : null}
      {!health && !error ? <Card aria-busy="true"><CardContent><Typography role="status">Loading system health…</Typography>{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} height={48} />)}</CardContent></Card> : null}
      {testResult ? <Alert ref={resultRef} tabIndex={-1} role="status" severity={testResult.severity}><AlertTitle>{testResult.title}</AlertTitle>{testResult.message}</Alert> : null}

      {health ? <>
        <Box component="section" aria-labelledby="overall-health-heading">
          <Typography id="overall-health-heading" component="h2" variant="h2" gutterBottom>Overall</Typography>
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, sm: 6, lg: 3 }}><HealthCard title="Application" status="Healthy" tone="success" detail={`${health.application.application} ${health.application.version} is running in ${health.application.environment}.`} /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 3 }}><HealthCard title="Database" status={health.database.reachable ? 'Healthy' : 'Unavailable'} tone={health.database.reachable ? 'success' : 'error'} detail={health.database.reachable ? `SQLite is reachable and migration schema ${health.database.currentSchemaVersion} is current.` : 'Operational persistence is unavailable.'} /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 3 }}><HealthCard title="Setup" status={health.setup.complete ? 'Ready' : 'Setup incomplete'} tone={health.setup.complete ? 'success' : 'warning'} detail={health.setup.complete ? 'Required configuration, credentials, and query are ready.' : 'The application is healthy, but setup still needs to be completed.'} /></Grid>
            <Grid size={{ xs: 12, sm: 6, lg: 3 }}><HealthCard title="Runtime" status={health.runtime.status === 'active' ? 'Active and current' : health.runtime.status === 'activationFailed' ? 'Activation failed' : 'No active generation'} tone={health.runtime.status === 'active' ? 'success' : 'warning'} detail={health.runtime.activeGenerationId === null ? 'Complete setup or resolve the configuration activation issue.' : `Active generation ${health.runtime.activeGenerationId}.`} /></Grid>
          </Grid>
        </Box>

        <Box component="section" aria-labelledby="integration-health-heading">
          <Typography id="integration-health-heading" component="h2" variant="h2" gutterBottom>Integrations</Typography>
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, md: 6 }}><HealthCard title="Azure DevOps" status={connectionLabel(health.azureDevOps.status)} tone={connectionTone(health.azureDevOps.status)} detail={health.azureDevOps.status === 'authenticationFailed' ? 'Verify or replace the Azure DevOps credential in configuration.' : health.azureDevOps.settingsReady ? 'Settings are configured. Status is based on the latest explicit verification.' : 'Complete Azure DevOps settings and saved-query confirmation.'}>
              <Typography variant="body2"><strong>Last verified:</strong> {formatDateTime(health.azureDevOps.lastVerifiedAtUtc)}</Typography>
              {admin ? <Button startIcon={testing === 'ado' ? <CircularProgress size={18} /> : <ScienceOutlined />} disabled={testing !== null} onClick={() => void testConnection('ado')}>Test Azure DevOps</Button> : null}
            </HealthCard></Grid>
            <Grid size={{ xs: 12, md: 6 }}><HealthCard title="AI Provider" status={connectionLabel(health.ai.status)} tone={connectionTone(health.ai.status)} detail={health.ai.status === 'notVerified' || health.ai.status === 'credentialMissing' ? 'Verify the configured provider credential.' : 'Status is based on the latest explicit verification; no provider request was made to render this page.'}>
              <Typography variant="body2"><strong>Provider / model:</strong> {health.ai.provider ?? 'Not configured'}{health.ai.model ? ` / ${health.ai.model}` : ''}</Typography>
              <Typography variant="body2"><strong>Last verified:</strong> {formatDateTime(health.ai.lastVerifiedAtUtc)}</Typography>
              {admin && health.aiDiagnostics ? <Box aria-label="Admin AI diagnostics" sx={{ p: 1.5, borderRadius: 1.5, bgcolor: 'action.hover' }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800 }}>Admin diagnostics</Typography>
                <Typography variant="body2"><strong>Active runtime:</strong> {health.aiDiagnostics.activeRuntimeProvider ?? 'Not active'}{health.aiDiagnostics.activeRuntimeModel ? ` / ${health.aiDiagnostics.activeRuntimeModel}` : ''}</Typography>
                <Typography variant="body2"><strong>Credential revision:</strong> {formatDateTime(health.aiDiagnostics.credentialRevisionAtUtc)}</Typography>
              </Box> : null}
              {admin ? <Button startIcon={testing === 'ai' ? <CircularProgress size={18} /> : <ScienceOutlined />} disabled={testing !== null || !health.ai.provider} onClick={() => void testConnection('ai')}>Verify AI Provider</Button> : null}
            </HealthCard></Grid>
          </Grid>
        </Box>

        <Box component="section" aria-labelledby="automation-health-heading">
          <Typography id="automation-health-heading" component="h2" variant="h2" gutterBottom>Automation</Typography>
          <HealthCard title="Scheduler" status={health.scheduler.status === 'manualOnly' ? 'Manual Only' : words(health.scheduler.status)} tone={health.scheduler.status === 'scheduled' ? 'success' : health.scheduler.status === 'activationFailed' ? 'error' : 'info'} detail={health.scheduler.status === 'manualOnly' ? 'Automatic scheduling is disabled; manual runs remain available.' : health.scheduler.status === 'waiting' ? 'The scheduler is waiting for an active configuration generation.' : health.scheduler.status === 'scheduled' ? `Next occurrence: ${formatDateTime(health.scheduler.nextOccurrenceUtc)} (${health.scheduler.timezone}).` : 'Resolve the configuration activation issue before scheduled execution can continue.'} />
        </Box>

        <Box component="section" aria-labelledby="recent-issues-heading">
          <Typography id="recent-issues-heading" component="h2" variant="h2" gutterBottom>Recent Issues</Typography>
          <Card variant="outlined"><CardContent>
            {health.recentFailures.length === 0 ? <><Typography component="h3" variant="h3">No recent operational failures</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>Failures from bounded persisted run history will appear here.</Typography></> : <Stack component="ul" spacing={2} sx={{ pl: 2.5, mb: 0 }}>{health.recentFailures.map((failure) => <li key={failure.runId}><Link component={RouterLink} to={`/runs/${failure.runId}`}>{failure.category}</Link><Typography variant="body2">{failure.message}</Typography><Typography variant="caption" color="text.secondary">{formatDateTime(failure.occurredAtUtc)}</Typography></li>)}</Stack>}
          </CardContent></Card>
        </Box>
      </> : null}
    </Stack>
  );
}
