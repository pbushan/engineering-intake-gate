import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import { adminUser, auditPage, completeSetup, installMockBackend, runSummary, systemHealth, viewerUser } from './mockBackend';

beforeEach(() => {
  apiClient.resetCsrf();
  vi.unstubAllGlobals();
});

it('SUP-UI-001 renders distinct health categories, verification age, Manual Only, and recent-run navigation', async () => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  window.history.replaceState({}, '', '/system-health');
  render(<App />);

  await screen.findByRole('heading', { name: 'Application' });
  for (const heading of ['Database', 'Setup', 'Runtime', 'Azure DevOps', 'AI Provider', 'Scheduler'])
    expect(screen.getByRole('heading', { name: heading })).toBeInTheDocument();
  expect(screen.getByText('Manual Only')).toBeInTheDocument();
  expect(screen.getAllByText(/Last verified:/)).toHaveLength(2);
  expect(screen.getByRole('link', { name: 'ProviderUnavailable' })).toHaveAttribute('href', `/runs/${runSummary.runId}`);
  expect(screen.getByRole('button', { name: 'Test Azure DevOps' })).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Verify AI Provider' })).toBeInTheDocument();
  expect(screen.getByLabelText('Admin AI diagnostics')).toHaveTextContent('openai / test-model');
});

it('SUP-UI-002 keeps setup incomplete separate from healthy infrastructure and hides mutations from Viewer', async () => {
  const health = { ...systemHealth,
    setup: { ...systemHealth.setup, status: 'incomplete' as const, complete: false, profileConfigured: false },
    runtime: { status: 'noActiveGeneration' as const, activeGenerationId: null, activationCurrent: false },
    scheduler: { status: 'waiting' as const, scheduled: false, timezone: null, nextOccurrenceUtc: null, activeGenerationId: null },
    ai: { ...systemHealth.ai, status: 'providerUnavailable' as const, verificationStatus: 'failed' as const, verificationDiagnostic: 'providerUnavailable' as const },
  };
  installMockBackend({ user: viewerUser, setup: { ...completeSetup, setupComplete: false }, health });
  window.history.replaceState({}, '', '/system-health');
  render(<App />);

  await screen.findByRole('heading', { name: 'Application' });
  expect(await screen.findAllByText('Healthy')).toHaveLength(2);
  expect(screen.getAllByText('Setup incomplete')).toHaveLength(2);
  expect(screen.getByText('Provider unavailable')).toBeInTheDocument();
  expect(screen.getByText(/scheduler is waiting/i)).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Test Azure DevOps' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Verify AI Provider' })).not.toBeInTheDocument();
  expect(screen.queryByLabelText('Admin AI diagnostics')).not.toBeInTheDocument();
});

it('SUP-UI-003 explicit Admin connection test refreshes persisted status', async () => {
  const user = userEvent.setup();
  const backend = installMockBackend({ user: adminUser, setup: completeSetup,
    health: { ...systemHealth, azureDevOps: { ...systemHealth.azureDevOps, status: 'notVerified', verificationStatus: 'neverVerified', lastVerifiedAtUtc: null } } });
  window.history.replaceState({}, '', '/system-health');
  render(<App />);
  await user.click(await screen.findByRole('button', { name: 'Test Azure DevOps' }));
  expect(await screen.findByText('Azure DevOps verified')).toBeInTheDocument();
  expect(backend.calls.filter((call) => call.path === '/api/ado/connection-tests')).toHaveLength(1);
  expect(backend.calls.filter((call) => call.path === '/api/support/health').length).toBeGreaterThanOrEqual(2);
});

it('SUP-UI-003 scheduled automation shows the backend-calculated next occurrence', async () => {
  installMockBackend({ user: viewerUser, setup: completeSetup, health: { ...systemHealth,
    scheduler: { status: 'scheduled', scheduled: true, timezone: 'America/Toronto', nextOccurrenceUtc: '2026-09-13T03:00:00Z', activeGenerationId: 7 },
  } });
  window.history.replaceState({}, '', '/system-health');
  render(<App />);

  expect(await screen.findByText('Scheduled')).toBeInTheDocument();
  expect(screen.getByText(/Next occurrence:.*America\/Toronto/)).toBeInTheDocument();
});

it('SUP-UI-004 connection-test failure is bounded and does not expose a secret form', async () => {
  const user = userEvent.setup();
  installMockBackend({ user: adminUser, setup: completeSetup, adoVerificationFails: true });
  window.history.replaceState({}, '', '/system-health');
  render(<App />);
  await user.click(await screen.findByRole('button', { name: 'Test Azure DevOps' }));
  expect(await screen.findByText('Azure DevOps test failed')).toBeInTheDocument();
  expect(screen.queryByLabelText(/credential|password|token/i)).not.toBeInTheDocument();
});

it('SUP-UI-005 audit is paged, filterable, expandable safe metadata with accurate System actor', async () => {
  const user = userEvent.setup();
  const backend = installMockBackend({ user: viewerUser, setup: completeSetup, auditPage: { ...auditPage, page: 1, totalPages: 2 } });
  window.history.replaceState({}, '', '/audit');
  render(<App />);

  const table = await screen.findByRole('table', { name: 'Control-plane audit history' });
  expect(within(table).getByText('System')).toBeInTheDocument();
  expect(within(table).getByText('AzureDevOpsConnectionVerificationSucceeded')).toBeInTheDocument();
  await user.click(within(table).getByRole('button', { name: /Expand audit event AzureDevOps/ }));
  expect(await screen.findByText('Safe event metadata')).toBeInTheDocument();
  expect(screen.getByText('verificationStatus')).toBeInTheDocument();
  await user.type(screen.getByLabelText('Actor username'), 'admin');
  await user.click(screen.getByRole('button', { name: 'Apply filters' }));
  await waitFor(() => expect(backend.calls.some((call) => call.path.includes('/api/audit?') && call.path.includes('actor=admin'))).toBe(true));
  expect(screen.queryByRole('button', { name: /rollback|undo/i })).not.toBeInTheDocument();
  expect(screen.getByRole('navigation', { name: 'Audit history pages' })).toBeInTheDocument();
});

it('SUP-UI-005 renders a missing historical actor as Unknown without inventing a user', async () => {
  const source = auditPage.items[0]!;
  installMockBackend({ user: viewerUser, setup: completeSetup, auditPage: { ...auditPage, items: [{
    ...source, id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    actor: { type: 'unknown', userId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', displayName: 'Unknown' },
  }] } });
  window.history.replaceState({}, '', '/audit');
  render(<App />);

  const table = await screen.findByRole('table', { name: 'Control-plane audit history' });
  expect(within(table).getByText('Unknown')).toBeInTheDocument();
});
