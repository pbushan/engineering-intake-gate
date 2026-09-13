import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import type { OperationalDecision, RunItemDetail } from '../api/contracts';
import {
  adminUser,
  installMockBackend,
  runDetail,
  runItemDetail,
  runPage,
  runSummary,
  viewerUser,
} from './mockBackend';

const renderAt = (path: string, options: Parameters<typeof installMockBackend>[0] = {}) => {
  const backend = installMockBackend({ user: adminUser, ...options });
  window.history.replaceState({}, '', path);
  return { backend, ...render(<App />) };
};

beforeEach(() => {
  apiClient.resetCsrf();
  vi.unstubAllGlobals();
});

describe('OPS-UI-001 role-aware operational navigation', () => {
  it('shows Analyze Ticket, Runs, and the run action to an Admin', async () => {
    renderAt('/runs');
    expect(await screen.findByRole('heading', { name: 'Runs' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Analyze Ticket' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Run Profile Now' })).toBeVisible();
  });

  it('lets a Viewer browse Runs without exposing execution controls', async () => {
    renderAt('/runs', { user: viewerUser });
    expect(await screen.findByRole('heading', { name: 'Runs' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Runs' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Analyze Ticket' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Run Profile Now' })).not.toBeInTheDocument();
  });

  it('redirects a Viewer away from the direct Analyze Ticket route', async () => {
    renderAt('/analyze', { user: viewerUser });
    expect(await screen.findByRole('heading', { name: 'Runs' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Analyze Ticket' })).not.toBeInTheDocument();
  });
});

describe('OPS-UI-002 governed Analyze Ticket execution', () => {
  it('submits a positive numeric work-item ID through the centralized client', async () => {
    const { backend } = renderAt('/analyze');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Azure DevOps work-item ID or URL'), '202');
    await user.click(screen.getByRole('button', { name: 'Analyze Ticket' }));
    expect(await screen.findByRole('heading', { name: /Run aaaaaaaa/ })).toBeVisible();
    const call = backend.calls.find((entry) => entry.path === '/api/runs/work-items' && entry.method === 'POST');
    expect(JSON.parse(call?.body ?? '{}')).toEqual({ workItemId: 202, workItemUrl: null });
  });

  it('submits a full supported ADO URL without reconstructing it', async () => {
    const { backend } = renderAt('/analyze');
    const url = 'https://ado.example.invalid/org/project/_workitems/edit/203';
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Azure DevOps work-item ID or URL'), url);
    await user.click(screen.getByRole('button', { name: 'Analyze Ticket' }));
    expect(await screen.findByRole('heading', { name: /Run aaaaaaaa/ })).toBeVisible();
    const call = backend.calls.find((entry) => entry.path === '/api/runs/work-items' && entry.method === 'POST');
    expect(JSON.parse(call?.body ?? '{}')).toEqual({ workItemId: null, workItemUrl: url });
  });

  it('rejects invalid input with an associated bounded form error', async () => {
    const { backend } = renderAt('/analyze');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Azure DevOps work-item ID or URL'), 'not a ticket');
    await user.click(screen.getByRole('button', { name: 'Analyze Ticket' }));
    expect(screen.getByText(/Enter a positive work-item ID/)).toBeVisible();
    expect(backend.calls.some((entry) => entry.path === '/api/runs/work-items')).toBe(false);
  });

  it('renders authoritative backend identity validation inline', async () => {
    renderAt('/analyze', { analyzeValidationFails: true });
    const user = userEvent.setup();
    const identity = await screen.findByLabelText('Azure DevOps work-item ID or URL');
    await user.type(identity, 'https://example.test/_workitems/edit/123');
    await user.click(screen.getByRole('button', { name: 'Analyze Ticket' }));
    expect(await screen.findByText('Enter a work-item URL for the configured Azure DevOps organization and project.')).toBeVisible();
    expect(identity).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByText(/Not Eligible/)).not.toBeInTheDocument();
  });

  it('renders NOT_ELIGIBLE as a distinct governance result with no override', async () => {
    renderAt('/analyze', { analyzeDecision: 'notEligible' });
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Azure DevOps work-item ID or URL'), '999');
    await user.click(screen.getByRole('button', { name: 'Analyze Ticket' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Not Eligible');
    expect(screen.getByText(/outside the configured intake query/)).toBeVisible();
    expect(screen.queryByText(/run anyway|force analyze|ignore eligibility/i)).not.toBeInTheDocument();
  });

  it('prevents duplicate Analyze Ticket submission while synchronous work is active', async () => {
    const { backend } = renderAt('/analyze', { operationDelayMs: 40 });
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Azure DevOps work-item ID or URL'), '202');
    const button = screen.getByRole('button', { name: 'Analyze Ticket' });
    await user.click(button);
    expect(screen.getByRole('button', { name: 'Analyzing ticket…' })).toBeDisabled();
    await screen.findByRole('heading', { name: /Run aaaaaaaa/ });
    expect(backend.calls.filter((entry) => entry.path === '/api/runs/work-items').length).toBe(1);
  });
});

describe('OPS-UI-003 Run Profile Now', () => {
  it('runs the active Dry Run configuration and prevents duplicate clicks', async () => {
    const { backend } = renderAt('/runs', { operationDelayMs: 40 });
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Runs' });
    await user.click(screen.getByRole('button', { name: 'Run Profile Now' }));
    expect(screen.getByRole('button', { name: 'Running profile…' })).toBeDisabled();
    await screen.findByRole('heading', { name: /Run aaaaaaaa/ });
    expect(backend.calls.filter((entry) => entry.path === '/api/runs' && entry.method === 'POST').length).toBe(1);
  });

  it('shows a bounded lease conflict and does not imply queuing', async () => {
    renderAt('/runs', { runConflict: true });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Run Profile Now' }));
    const alert = (await screen.findByText('A run is already active for this profile')).closest('[role="alert"]');
    expect(alert).not.toBeNull();
    expect(alert).toHaveTextContent('A run is already active for this profile');
    expect(alert).toHaveTextContent('No additional run was queued');
    expect(alert).not.toHaveTextContent(/lease|lock/i);
  });
});

describe('OPS-UI-004 server-paged run history', () => {
  it('loads only the first backend page and renders persisted values', async () => {
    const { backend } = renderAt('/runs');
    const table = await screen.findByRole('table', { name: 'Run history' });
    expect(within(table).getByText('aaaaaaaa')).toBeVisible();
    expect(within(table).getByText('1 evaluated')).toBeVisible();
    expect(backend.calls.some((entry) => entry.path.includes('/api/runs?page=1&pageSize=20'))).toBe(true);
  });

  it('uses backend paging metadata and preserves filters while paging', async () => {
    const page = { ...runPage, totalCount: 40, totalPages: 2 };
    const { backend } = renderAt('/runs?workItemId=101', { runPage: page });
    await screen.findByRole('table', { name: 'Run history' });
    await userEvent.click(screen.getByRole('button', { name: 'Go to page 2' }));
    await waitFor(() => expect(backend.calls.some((entry) => entry.path.includes('page=2') && entry.path.includes('workItemId=101'))).toBe(true));
  });

  it('applies invocation/status/work-item filters on the backend request', async () => {
    const { backend } = renderAt('/runs');
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Run history' });
    await user.click(screen.getByLabelText('Invocation'));
    await user.click(screen.getByRole('option', { name: 'Analyze Ticket' }));
    await user.click(screen.getByLabelText('Run status'));
    await user.click(screen.getByRole('option', { name: 'Completed with errors' }));
    await user.type(screen.getByLabelText('Work-item ID'), '404');
    await user.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(backend.calls.some((entry) => entry.path.includes('invocationType=manualWorkItem') && entry.path.includes('status=completedWithErrors') && entry.path.includes('workItemId=404'))).toBe(true));
  });

  it('renders the unfiltered empty-history state', async () => {
    renderAt('/runs', { runPage: { ...runPage, totalCount: 0, totalPages: 0, items: [] } });
    expect(await screen.findByText('No runs yet.')).toBeVisible();
  });

  it('renders a distinct empty-filter-result state', async () => {
    renderAt('/runs?workItemId=999', { runPage: { ...runPage, totalCount: 0, totalPages: 0, items: [] } });
    expect(await screen.findByText('No runs match these filters.')).toBeVisible();
  });
});

describe('OPS-UI-005 run detail', () => {
  it('renders metadata, aggregate outcomes, usage, and cost', async () => {
    renderAt(`/runs/${runSummary.runId}`);
    expect(await screen.findByRole('heading', { name: /Run aaaaaaaa/ })).toBeVisible();
    expect(screen.getByText(/Run Profile Now/)).toBeVisible();
    expect(screen.getAllByText('Engineering Ready').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/1,500/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/0\.0042/).length).toBeGreaterThan(0);
  });

  it('handles a historical null generation without inventing metadata', async () => {
    renderAt(`/runs/${runSummary.runId}`, { runDetail: { ...runDetail, summary: { ...runSummary, configurationGenerationId: null }, profileVersion: null } });
    await screen.findByRole('heading', { name: /Run aaaaaaaa/ });
    await userEvent.click(screen.getByRole('button', { name: 'Technical generation details' }));
    expect(screen.getByText('Historical generation unavailable')).toBeVisible();
    expect(screen.getByText('Historical value unavailable')).toBeVisible();
  });

  it.each([
    ['pass', 'Engineering Ready'],
    ['fail', 'Intake Incomplete'],
    ['error', 'Error'],
    ['notEligible', 'Not Eligible'],
  ] as const)('renders the backend %s decision as %s', async (decision, label) => {
    renderAt(`/runs/${runSummary.runId}`, { runDetail: { ...runDetail, items: [{ ...runDetail.items[0]!, decision, decisionLabel: label }] } });
    await screen.findByRole('heading', { name: /Run aaaaaaaa/ });
    expect(screen.getAllByText(label).length).toBeGreaterThan(0);
  });

  it('navigates from a run item to its evaluation detail', async () => {
    renderAt(`/runs/${runSummary.runId}`);
    await userEvent.click(await screen.findByRole('link', { name: 'Open evaluation for work item 101' }));
    expect(await screen.findByRole('heading', { name: 'Evaluation detail' })).toBeVisible();
  });

  it('states clearly when a persisted run has no evaluation rows', async () => {
    renderAt(`/runs/${runSummary.runId}`, { runDetail: { ...runDetail, items: [] } });
    expect(await screen.findByText('This run has no persisted evaluation rows.')).toBeVisible();
  });

  it('does not infer aggregate duplicate suppression when the backend count is zero', async () => {
    renderAt(`/runs/${runSummary.runId}`);
    await screen.findByRole('heading', { name: /Run aaaaaaaa/ });
    expect(screen.getByText('No suppression reported')).toBeVisible();
    expect(screen.queryByText('Duplicate Update Suppressed')).not.toBeInTheDocument();
  });
});

describe('OPS-UI-006 evaluation detail', () => {
  const renderEvaluation = (detail: RunItemDetail = runItemDetail) => renderAt(`/runs/${runSummary.runId}/items/${runItemDetail.evaluationId}`, { runItemDetail: detail });

  it('renders criteria, deficiencies, and ambiguities without numeric scoring', async () => {
    renderEvaluation();
    expect(await screen.findByRole('heading', { name: 'Evaluation detail' })).toBeVisible();
    expect(screen.getByText('reproduction — Satisfied')).toBeVisible();
    expect(screen.getByText('Environment details are missing.')).toBeVisible();
    expect(screen.getByText('Provide the affected environment.')).toBeVisible();
    expect(screen.getByText('One step is unclear.')).toBeVisible();
    expect(screen.queryByText(/\/100/)).not.toBeInTheDocument();
  });

  it('separates proposed effects from confirmed Dry Run actual effects', async () => {
    renderEvaluation();
    await screen.findByRole('heading', { name: 'Evaluation detail' });
    const effects = screen.getByLabelText('Azure DevOps effects');
    expect(within(effects).getByRole('heading', { name: 'Proposed ADO Changes' })).toBeVisible();
    expect(within(effects).getByText('Add tag')).toBeVisible();
    expect(within(effects).getByText('Dry Run — no Azure DevOps modifications were made.')).toBeVisible();
  });

  it('renders actual effects only from backend actualEffects', async () => {
    renderEvaluation({ ...runItemDetail, effectiveMode: 'live', actualEffects: [{ type: 'addTag', target: 'intakeTags', tag: 'Engineering Ready' }] });
    await screen.findByRole('heading', { name: 'Evaluation detail' });
    const actual = screen.getByRole('heading', { name: 'Actual ADO Changes' }).closest('.MuiCard-root');
    expect(actual).not.toBeNull();
    expect(within(actual as HTMLElement).getByText('Add tag')).toBeVisible();
    expect(within(actual as HTMLElement).queryByText(/Dry Run/)).not.toBeInTheDocument();
  });

  it('shows suppression only when updateSuppressed is explicitly true', async () => {
    renderEvaluation({ ...runItemDetail, updateSuppressed: true, suppressionReason: 'MateriallyUnchanged' });
    expect(await screen.findByText('Duplicate Update Suppressed')).toBeVisible();
    expect(screen.getByText('MateriallyUnchanged')).toBeVisible();
  });

  it('does not label a generic no-op as duplicate suppression', async () => {
    renderEvaluation({ ...runItemDetail, proposedEffects: [], actualEffects: [], updateSuppressed: false, suppressionReason: null });
    await screen.findByRole('heading', { name: 'Evaluation detail' });
    expect(screen.queryByText('Duplicate Update Suppressed')).not.toBeInTheDocument();
    expect(screen.getByText('No Azure DevOps changes were proposed.')).toBeVisible();
  });

  it('formats usage/cost and uses the backend safe ADO URL with external-link protection', async () => {
    renderEvaluation();
    await screen.findByRole('heading', { name: 'Evaluation detail' });
    expect(screen.getByText('1,250')).toBeVisible();
    expect(screen.getByText('250')).toBeVisible();
    expect(screen.getByText('1,500')).toBeVisible();
    expect(screen.getByText(/\$0\.0042/)).toBeVisible();
    const link = screen.getByRole('link', { name: /Open work item 101 in Azure DevOps/ });
    expect(link).toHaveAttribute('href', runItemDetail.azureDevOpsUrl);
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it.each([
    ['pass', 'Engineering Ready'],
    ['fail', 'Intake Incomplete'],
    ['error', 'Error'],
    ['notEligible', 'Not Eligible'],
  ] as const)('keeps evaluation outcome %s semantically distinct', async (decision: OperationalDecision, label) => {
    renderEvaluation({ ...runItemDetail, decision, decisionLabel: label, eligibility: decision === 'notEligible' ? 'notEligible' : decision === 'error' ? 'unknown' : 'eligible', processingStatus: decision === 'error' ? 'error' : 'completed' });
    expect((await screen.findAllByText(label)).length).toBeGreaterThan(0);
  });

  it('presents bounded backend error classifications without raw payload material', async () => {
    renderEvaluation({ ...runItemDetail, decision: 'error', decisionLabel: 'Technical/System Failure', errors: [{ category: 'ProviderUnavailable', message: 'The evaluation provider could not complete the request.' }] });
    const alert = (await screen.findByRole('heading', { name: 'Safe error details' })).closest('[role="alert"]');
    expect(alert).not.toBeNull();
    expect(alert).toHaveTextContent('ProviderUnavailable');
    expect(alert).toHaveTextContent('could not complete the request');
    expect(document.body).not.toHaveTextContent(/stack trace|provider body|SELECT \*/i);
  });

  it('allows a Viewer to read run and evaluation detail with no mutation controls', async () => {
    renderAt(`/runs/${runSummary.runId}/items/${runItemDetail.evaluationId}`, { user: viewerUser });
    expect(await screen.findByRole('heading', { name: 'Evaluation detail' })).toBeVisible();
    expect(screen.queryByRole('button', { name: /run profile|analyze ticket/i })).not.toBeInTheDocument();
  });
});
