import ErrorOutlineRounded from '@mui/icons-material/ErrorOutlineRounded';
import LaunchRounded from '@mui/icons-material/LaunchRounded';
import { Alert, Box, Card, CardContent, Chip, Grid, Link, Stack, Typography } from '@mui/material';
import type {
  Effect,
  EstimatedCost,
  OperationalDecision,
  OperationalRunStatus,
  RunTriggerType,
  TokenUsage,
} from '../api/contracts';

const numeric = (value: number | string): number => Number(value);

export const formatInteger = (value: number | string | null | undefined): string =>
  value === null || value === undefined || !Number.isFinite(numeric(value))
    ? '—'
    : new Intl.NumberFormat().format(numeric(value));

export const formatDateTime = (value: string | null): string =>
  value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—';

export const formatDuration = (value: number | string | null): string => {
  if (value === null) return '—';
  const milliseconds = numeric(value);
  if (milliseconds < 1000) return `${milliseconds} ms`;
  if (milliseconds < 60_000) return `${(milliseconds / 1000).toFixed(1)} s`;
  return `${Math.floor(milliseconds / 60_000)}m ${Math.round((milliseconds % 60_000) / 1000)}s`;
};

export type CostDisplayContext = 'detail' | 'summary';

export const formatCost = (cost: EstimatedCost | null, context: CostDisplayContext = 'detail'): string => {
  if (!cost) return '—';
  const amount = Number(cost.amount);
  if (!Number.isFinite(amount)) return '—';
  const absolute = Math.abs(amount);
  const fractionDigits = amount === 0
    ? 2
    : context === 'summary' && absolute >= 0.01
      ? 2
      : absolute < 0.0001 ? 8 : 4;
  try {
    const formatted = new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: cost.currency,
      currencyDisplay: 'narrowSymbol',
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
    }).format(amount);
    return `${formatted} ${cost.currency}`;
  } catch {
    return `${amount.toFixed(fractionDigits)} ${cost.currency}`;
  }
};

export const costCoverage = (cost: EstimatedCost | null): string | undefined =>
  cost && !cost.complete ? 'Partial estimate' : undefined;

export const invocationLabel = (value: RunTriggerType): string => ({
  scheduled: 'Scheduled',
  manualIncremental: 'Run Profile Now',
  manualWorkItem: 'Analyze Ticket',
})[value];

export const actorLabel = (actor: { type: string; username: string | null }): string => {
  if (actor.type === 'system') return actor.username || 'System';
  if (actor.type === 'historicalUnknown') return 'Historical actor unavailable';
  return actor.username || 'User';
};

const decisionPresentation: Record<OperationalDecision, { label: string; color: 'success' | 'warning' | 'error' | 'default' }> = {
  pass: { label: 'Engineering Ready', color: 'success' },
  fail: { label: 'Intake Incomplete', color: 'warning' },
  error: { label: 'Error', color: 'error' },
  notEligible: { label: 'Not Eligible', color: 'default' },
};

export function DecisionChip({ decision }: { decision: OperationalDecision }) {
  const presentation = decisionPresentation[decision];
  return <Chip size="small" color={presentation.color} variant={decision === 'notEligible' ? 'outlined' : 'filled'} label={presentation.label} />;
}

const runPresentation: Record<OperationalRunStatus, { label: string; color: 'success' | 'warning' | 'error' | 'info' }> = {
  running: { label: 'Running', color: 'info' },
  completed: { label: 'Completed', color: 'success' },
  completedWithErrors: { label: 'Completed with errors', color: 'warning' },
  error: { label: 'Execution error', color: 'error' },
};

export function RunStatusChip({ status }: { status: OperationalRunStatus }) {
  const presentation = runPresentation[status];
  return <Chip size="small" color={presentation.color} label={presentation.label} />;
}

export function AzureDevOpsLink({ href, workItemId }: { href: string | null; workItemId: string }) {
  if (!href) return <Typography component="span" color="text.secondary">Link unavailable</Typography>;
  return (
    <Link href={href} target="_blank" rel="noopener noreferrer" sx={{ display: 'inline-flex', gap: 0.5, alignItems: 'center' }}>
      Open work item {workItemId} in Azure DevOps <LaunchRounded fontSize="inherit" aria-hidden />
    </Link>
  );
}

export function UsageCost({ usage, cost, showCost = true }: { usage: TokenUsage | null; cost: EstimatedCost | null; showCost?: boolean }) {
  return (
    <Grid container spacing={2} aria-label="AI usage and estimated cost">
      <Metric label="Input tokens" value={usage ? formatInteger(usage.inputTokens) : '—'} />
      <Metric label="Output tokens" value={usage ? formatInteger(usage.outputTokens) : '—'} />
      <Metric label="Total tokens" value={usage ? formatInteger(usage.totalTokens) : '—'} />
      {showCost ? <Metric label="Estimated AI cost" value={formatCost(cost)} detail={costCoverage(cost)} /> : null}
    </Grid>
  );
}

export function Metric({ label, value, detail }: { label: string; value: string; detail?: string | undefined }) {
  return (
    <Grid size={{ xs: 6, sm: 3 }}>
      <Box>
        <Typography variant="caption" color="text.secondary">{label}</Typography>
        <Typography variant="h3" sx={{ mt: 0.25 }}>{value}</Typography>
        {detail ? <Typography variant="caption" color="text.secondary">{detail}</Typography> : null}
      </Box>
    </Grid>
  );
}

const effectLabel = (effect: Effect): string => ({ removeTag: 'Remove tag', addTag: 'Add tag', postComment: 'Post validator comment' })[effect.type];

export function EffectsCard({
  title,
  effects,
  emptyMessage,
  dryRun,
}: {
  title: string;
  effects: Effect[];
  emptyMessage: string;
  dryRun?: boolean;
}) {
  return (
    <Card variant="outlined" sx={{ height: '100%' }}>
      <CardContent>
        <Typography component="h2" variant="h2">{title}</Typography>
        {effects.length > 0 ? (
          <Stack component="ul" spacing={1} sx={{ pl: 2.5, mb: 0 }}>
            {effects.map((effect, index) => (
              <Box component="li" key={`${effect.type}-${effect.target}-${effect.tag ?? ''}-${index}`}>
                <Typography><strong>{effectLabel(effect)}</strong>{effect.tag ? ` — ${effect.tag}` : ''}</Typography>
                <Typography variant="caption" color="text.secondary">Target: {effect.target}</Typography>
              </Box>
            ))}
          </Stack>
        ) : dryRun ? (
          <Alert severity="info" sx={{ mt: 2 }}>Dry Run — no Azure DevOps modifications were made.</Alert>
        ) : (
          <Typography color="text.secondary" sx={{ mt: 2 }}>{emptyMessage}</Typography>
        )}
      </CardContent>
    </Card>
  );
}

export function SafeErrors({ errors }: { errors: Array<{ category: string; message: string }> }) {
  if (errors.length === 0) return null;
  return (
    <Alert severity="error" icon={<ErrorOutlineRounded />}>
      <Typography component="h2" variant="h3">Safe error details</Typography>
      <Stack component="ul" spacing={0.5} sx={{ pl: 2.5, mb: 0 }}>
        {errors.map((error, index) => <li key={`${error.category}-${index}`}><strong>{error.category}:</strong> {error.message}</li>)}
      </Stack>
    </Alert>
  );
}
