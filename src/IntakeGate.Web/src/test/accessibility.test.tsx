import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import type { SetupStatus } from '../api/contracts';
import { adminUser, completeDraft, completeSetup, incompleteSetup, installMockBackend, readyAzureDevOpsSettings, runItemDetail, runSummary, viewerUser } from './mockBackend';

beforeEach(() => {
  apiClient.resetCsrf();
  vi.unstubAllGlobals();
});

it.each([
  ['login', '/login', { bootstrapAvailable: false }, 'Welcome back'],
  ['bootstrap', '/bootstrap', { bootstrapAvailable: true }, 'Create the first administrator'],
  ['authenticated shell', '/', { user: adminUser, setup: completeSetup }, 'Engineering Intake Gate'],
  ['profile configuration', '/configuration', { user: adminUser, setup: completeSetup }, 'Edit profile and configuration'],
  ['setup-incomplete Viewer', '/setup-required', { user: viewerUser, setup: incompleteSetup }, 'This installation is not ready yet'],
] as const)('A11Y core %s surface has no automated axe violations', async (_name, path, backend, heading) => {
  installMockBackend(backend);
  window.history.replaceState({}, '', path);
  const { container } = render(<App />);
  await screen.findByRole('heading', { name: new RegExp(heading) });
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});

const staged = (changes: Partial<SetupStatus>): SetupStatus => ({
  ...completeSetup,
  profileConfigured: false,
  onboardingDraftExists: true,
  runtimeActivationCurrent: false,
  setupComplete: false,
  ...changes,
});

it.each([
  ['Welcome', { ...incompleteSetup, lastVisitedStep: 'Welcome' }, undefined, 'Welcome'],
  ['ADO', { ...incompleteSetup, lastVisitedStep: 'AzureDevOps' }, undefined, 'Azure DevOps credential'],
  ['AI/model', staged({ onboardingDraftExists: false, profileDetailsComplete: false, policyDetailsComplete: false, aiModelConfigured: false, lastVisitedStep: 'Ai' }), undefined, 'Select a model'],
  ['system defaults', staged({ lastVisitedStep: 'SystemDefaults' }), { draft: completeDraft }, 'System defaults'],
  ['profile/policy', staged({ profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Profile' }), { draft: completeDraft }, 'Profile basics'],
  ['optional schedule', staged({ lastVisitedStep: 'OptionalSchedule' }), { draft: completeDraft }, 'Optional schedule'],
  ['Review', staged({ lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft }, 'Review authoritative setup'],
] as const)('UI-014 Phase 4B %s surface has no automated axe violations', async (_name, setup, options, heading) => {
  installMockBackend({ user: adminUser, setup, ...options });
  window.history.replaceState({}, '', '/setup');
  const { container } = render(<App />);
  await screen.findByRole('heading', { name: heading });
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});

it('UI-014 Phase 4B saved-query preview has no automated axe violations', async () => {
  installMockBackend({ user: adminUser, setup: staged({ azureDevOpsSavedQueryConfirmed: false, lastVisitedStep: 'SavedQuery' }), adoSettings: { ...readyAzureDevOpsSettings, savedQueryId: null, queryConfirmed: false, queryValidatedAtUtc: null } });
  window.history.replaceState({}, '', '/setup');
  const { container } = render(<App />);
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText(/^Saved-query URL or GUID/), '11111111-1111-1111-1111-111111111111');
  await user.click(screen.getByRole('button', { name: 'Validate query' }));
  await screen.findByRole('table', { name: 'Saved query preview' });
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});

it('UI-014 actionable validation summary and inline errors have no automated axe violations', async () => {
  const draft = {
    ...completeDraft,
    values: { ...completeDraft.values!, policyUrl: null, policy: { id: null, version: null, criteria: [] } },
  };
  installMockBackend({ user: adminUser, setup: staged({ profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Profile' }), draft });
  window.history.replaceState({}, '', '/setup');
  const { container } = render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'Save & continue' }));
  await screen.findByText('Complete the highlighted fields');
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});

it.each([
  ['Analyze Ticket', '/analyze', 'Analyze Ticket'],
  ['Runs', '/runs', 'Runs'],
  ['Run detail', `/runs/${runSummary.runId}`, 'Run aaaaaaaa'],
  ['Evaluation detail', `/runs/${runSummary.runId}/items/${runItemDetail.evaluationId}`, 'Evaluation detail'],
] as const)('OPS-UI-007 %s surface has no automated axe violations', async (_name, path, heading) => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  window.history.replaceState({}, '', path);
  const { container } = render(<App />);
  await screen.findByRole('heading', { name: heading });
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});

it.each([
  ['Home', '/home', 'Engineering Intake Gate'],
  ['System Health', '/system-health', 'System Health'],
  ['Audit', '/audit', 'Audit'],
  ['Help', '/help', 'Help'],
] as const)('SUP-UI-006 %s surface has no automated axe violations', async (_name, path, heading) => {
  installMockBackend({ user: adminUser, setup: completeSetup });
  window.history.replaceState({}, '', path);
  const { container } = render(<App />);
  await screen.findByRole('heading', { name: heading });
  const results = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations).toEqual([]);
});
