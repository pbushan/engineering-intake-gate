import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { axe } from 'vitest-axe';
import { App } from '../App';
import { apiClient } from '../api/client';
import type { HomeSummary } from '../api/contracts';
import { adminUser, completeSetup, homeSummary, installMockBackend, runSummary, viewerUser } from './mockBackend';

beforeEach(() => {
  apiClient.resetCsrf();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/home');
});

it('HOME-UI-001 renders the 30-day backend aggregate with locked labels, cost, warning, and recent-run link', async () => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  render(<App />);

  expect(await screen.findByRole('heading', { name: 'Last 30 days' })).toBeVisible();
  const metrics = screen.getByRole('region', { name: 'Last 30 days' });
  expect(metrics).toHaveTextContent('75.0%');
  expect(metrics).toHaveTextContent('Tickets Evaluated');
  expect(metrics).toHaveTextContent('Engineering Ready');
  expect(metrics).toHaveTextContent('Intake Incomplete');
  expect(metrics).toHaveTextContent('Technical/system failures');
  expect(screen.getByText('Duplicate Updates Suppressed').parentElement).toHaveTextContent('2');
  expect(screen.getByText('Estimated AI Cost').parentElement).toHaveTextContent('$0.01 USD');
  expect(screen.getByText('Recent run needs attention')).toBeVisible();
  expect(screen.getByRole('link', { name: `Open run ${runSummary.runId}` })).toHaveAttribute('href', `/runs/${runSummary.runId}`);
});

it('HOME-UI-002 switches only among the 7, 30, and 90 day server summaries', async () => {
  const seven = { ...homeSummary, windowDays: 7, evaluatedCount: 7 } satisfies HomeSummary;
  const ninety = { ...homeSummary, windowDays: 90, evaluatedCount: 90 } satisfies HomeSummary;
  const backend = installMockBackend({ user: adminUser, setup: completeSetup, homeSummaries: { 7: seven, 90: ninety } });
  const user = userEvent.setup();
  render(<App />);
  await screen.findByRole('heading', { name: 'Last 30 days' });

  await user.click(screen.getByRole('button', { name: 'Last 7 days' }));
  expect(await screen.findByRole('heading', { name: 'Last 7 days' })).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Last 90 days' }));
  expect(await screen.findByRole('heading', { name: 'Last 90 days' })).toBeVisible();
  expect(backend.calls.some((call) => call.path === '/api/home/summary?windowDays=7')).toBe(true);
  expect(backend.calls.some((call) => call.path === '/api/home/summary?windowDays=90')).toBe(true);
});

it('HOME-UI-003 represents a zero denominator and empty window without implying zero quality', async () => {
  installMockBackend({ user: viewerUser, setup: completeSetup, homeSummary: {
    ...homeSummary,
    evaluatedCount: 0,
    engineeringReadyCount: 0,
    intakeIncompleteCount: 0,
    errorCount: 0,
    notEligibleCount: 0,
    duplicateUpdatesSuppressedCount: 0,
    engineeringReadyRate: { numerator: 0, denominator: 0, percentage: null },
    estimatedAiCost: { amount: null, currency: null, evaluationsWithEstimate: 0, evaluationsWithCompleteEstimate: 0, evaluationsWithPartialEstimate: 0, evaluationsWithoutEstimate: 0, complete: true },
    recentRuns: [],
  } });
  render(<App />);

  await screen.findByText('No evaluated tickets in the last 30 days.');
  expect(screen.getByLabelText('Engineering-Ready Rate unavailable')).toBeVisible();
  expect(screen.getByText('No Engineering Ready or Intake Incomplete assessments in this window.')).toBeVisible();
  expect(screen.getByText('No runs started in the selected window.')).toBeVisible();
  expect(screen.getByText('Estimated AI Cost').parentElement).toHaveTextContent('$0.00 USD');
});

it('AI-COST-UI-001 distinguishes unknown and partial dashboard coverage', async () => {
  const partial = {
    ...homeSummary,
    evaluatedCount: 8,
    estimatedAiCost: {
      amount: 4.87,
      currency: 'USD',
      evaluationsWithEstimate: 7,
      evaluationsWithCompleteEstimate: 7,
      evaluationsWithPartialEstimate: 0,
      evaluationsWithoutEstimate: 1,
      complete: false,
    },
  } satisfies HomeSummary;
  const backend = installMockBackend({ user: viewerUser, setup: completeSetup, homeSummaries: {
    30: partial,
    7: { ...partial, windowDays: 7, estimatedAiCost: { ...partial.estimatedAiCost, amount: null, currency: null, evaluationsWithEstimate: 0, evaluationsWithCompleteEstimate: 0, evaluationsWithoutEstimate: 8 } },
    90: { ...partial, windowDays: 90, estimatedAiCost: { ...partial.estimatedAiCost, amount: 0.0034, complete: true, evaluationsWithCompleteEstimate: 8, evaluationsWithEstimate: 8, evaluationsWithoutEstimate: 0 } },
  } });
  render(<App />);

  const cost = (await screen.findByText('Estimated AI Cost')).parentElement!;
  expect(cost).toHaveTextContent('$4.87 USD');
  expect(cost).toHaveTextContent('7 of 8 evaluated ticket(s) fully costed.');
  await userEvent.click(screen.getByRole('button', { name: 'Last 7 days' }));
  expect(await screen.findByText('No pricing estimates available.')).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Last 90 days' }));
  expect((await screen.findByText('Estimated AI Cost')).parentElement).toHaveTextContent('$0.0034 USD');
  expect(backend.calls.some((call) => call.path === '/api/home/summary?windowDays=7')).toBe(true);
});

it('HOME-UI-004 shows healthy state and keeps Viewer free of mutation actions', async () => {
  installMockBackend({ user: viewerUser, setup: completeSetup, homeSummary: { ...homeSummary, healthWarnings: [] } });
  render(<App />);

  expect(await screen.findByText('No health warnings')).toBeVisible();
  expect(screen.queryByRole('link', { name: 'Analyze Ticket' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Run Profile Now' })).not.toBeInTheDocument();
});

it('HOME-UI-005 exposes only navigation to existing execution flows for Admin', async () => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  render(<App />);

  const actions = await screen.findByRole('heading', { name: 'Quick actions' });
  const card = actions.parentElement!;
  expect(within(card).getByRole('link', { name: 'Analyze Ticket' })).toHaveAttribute('href', '/analyze');
  expect(within(card).getByRole('link', { name: 'Run Profile Now' })).toHaveAttribute('href', '/runs');
});

it('HOME-UI-006 announces loading and renders a recoverable backend failure', async () => {
  installMockBackend({ user: adminUser, setup: completeSetup, homeSummaryFails: true, homeSummaryDelayMs: 20 });
  render(<App />);

  expect(await screen.findByText('Loading 30-day operational summary…')).toBeVisible();
  expect(await screen.findByRole('alert')).toHaveTextContent('operational summary is temporarily unavailable');
  expect(screen.getByRole('button', { name: 'Try again' })).toBeVisible();
});

it('HOME-UI-007 Home has no automated axe violations', async () => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  const { container } = render(<App />);
  await screen.findByRole('heading', { name: 'Last 30 days' });
  await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});
