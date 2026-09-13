import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import type { SetupStatus } from '../api/contracts';
import {
  adminUser,
  completeDraft,
  completeSetup,
  incompleteSetup,
  installMockBackend,
  profileState,
  readyAiSettings,
  readyAzureDevOpsSettings,
  verifiedCredential,
} from './mockBackend';

const renderSetup = (setup: SetupStatus, options: Parameters<typeof installMockBackend>[0] = {}) => {
  const backend = installMockBackend({ user: adminUser, setup, ...options });
  window.history.replaceState({}, '', '/setup');
  render(<App />);
  return backend;
};

const staged = (changes: Partial<SetupStatus> = {}): SetupStatus => ({
  ...completeSetup,
  profileConfigured: false,
  onboardingDraftExists: true,
  runtimeActivationCurrent: false,
  setupComplete: false,
  ...changes,
});

describe('Phase 4B authoritative setup wizard', () => {
  beforeEach(() => {
    apiClient.resetCsrf();
    localStorage.clear();
    sessionStorage.clear();
    vi.unstubAllGlobals();
  });

  it('UI-011 enters on Welcome, explains Dry Run, omits Production controls, and persists navigation', async () => {
    const backend = renderSetup(incompleteSetup);
    expect(await screen.findByRole('heading', { name: 'Welcome' })).toBeVisible();
    expect(screen.getByText(/does not confirm a defect/)).toBeVisible();
    expect(screen.getByText(/No Azure DevOps modifications will be made/)).toBeVisible();
    expect(screen.queryByRole('switch', { name: /production/i })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Continue setup' }));
    expect(await screen.findByRole('heading', { name: 'Azure DevOps connection' })).toBeVisible();
    await waitFor(() => expect(backend.calls.some((call) => call.path === '/api/setup/progress' && call.body?.includes('AzureDevOps'))).toBe(true));
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('UI-011 treats lastVisitedStep as preference and corrects Review to incomplete Profile', async () => {
    renderSetup(staged({ onboardingDraftExists: true, profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft });
    expect(await screen.findByRole('heading', { name: 'Profile basics' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Review authoritative setup' })).not.toBeInTheDocument();
  });

  it('UI-008 loads server fields and initializes a persisted draft without browser constants', async () => {
    const setup = staged({ onboardingDraftExists: false, profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'SystemDefaults' });
    const backend = renderSetup(setup);
    expect(await screen.findByText('aiRuntime.timeoutSeconds')).toBeVisible();
    expect(await screen.findByText(/Persisted revision 1/)).toBeVisible();
    expect(backend.calls.some((call) => call.path === '/api/setup/profile-draft/initialize')).toBe(true);
  });

  it('SEC-003 saves and clears a local ADO PAT, then verifies through the backend', async () => {
    const backend = renderSetup({ ...incompleteSetup, lastVisitedStep: 'AzureDevOps' });
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Azure DevOps credential' });
    await user.type(screen.getByLabelText(/^Organization URL/), 'https://dev.azure.example/contoso');
    await user.type(screen.getByLabelText(/^Project/), 'Engineering');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));
    await user.type(screen.getByLabelText('Personal access token'), 'temporary-pat-value');
    await user.click(screen.getByRole('button', { name: 'Save credential' }));
    expect(screen.getByLabelText('Replacement PAT')).toHaveValue('');
    const credentialCall = backend.calls.find((call) => call.path === '/api/ado/credential/local');
    expect(credentialCall?.body).toContain('temporary-pat-value');
    expect(JSON.stringify(localStorage)).not.toContain('temporary-pat-value');
    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(await screen.findByText('Azure DevOps connection verified.')).toBeVisible();
  });

  it('SEC-006 stores only an ADO environment variable name', async () => {
    const backend = renderSetup({ ...incompleteSetup, lastVisitedStep: 'AzureDevOps' });
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Azure DevOps credential' });
    await user.click(screen.getByLabelText('Environment-variable reference'));
    await user.type(screen.getByLabelText('Environment variable name'), 'INTAKE_ADO_PAT');
    await user.click(screen.getByRole('button', { name: 'Save credential' }));
    expect(backend.calls.find((call) => call.path === '/api/ado/credential/environment')?.body).toBe('{"environmentVariableName":"INTAKE_ADO_PAT"}');
    expect(screen.getByText('Environment reference')).toBeVisible();
  });

  it('ADO-003 shows a bounded authentication failure without provider payloads', async () => {
    renderSetup(staged({ azureDevOpsCredentialVerified: false, lastVisitedStep: 'AzureDevOps' }), { adoCredential: { ...verifiedCredential, verificationStatus: 'neverVerified', lastVerifiedAtUtc: null }, adoVerificationFails: true });
    await screen.findByRole('heading', { name: 'Azure DevOps credential' });
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(await screen.findByText('The request could not be completed safely.')).toBeVisible();
    expect(screen.queryByText(/provider stack|response body/i)).not.toBeInTheDocument();
  });

  it('AI-009 shows a bounded provider verification failure without exposing the submitted key', async () => {
    renderSetup(staged({ aiCredentialVerified: false, aiModelConfigured: false, lastVisitedStep: 'Ai' }), { aiCredentials: { openai: { ...verifiedCredential, verificationStatus: 'neverVerified', lastVerifiedAtUtc: null } }, aiVerificationFails: true });
    await screen.findByRole('heading', { name: 'OpenAI credential' });
    await userEvent.click(screen.getByRole('button', { name: 'Verify provider' }));
    expect(await screen.findByText('The request could not be completed safely.')).toBeVisible();
    expect(screen.queryByText(/provider stack|response body|SYNTH_/i)).not.toBeInTheDocument();
  });

  it.each(['openai', 'anthropic'] as const)('AI-008 AI-010 configures, verifies, discovers, validates, and confirms %s', async (provider) => {
    const backend = renderSetup(staged({ aiCredentialConfigured: false, aiCredentialVerified: false, aiModelConfigured: false, requiredAiCredentialSlot: null, onboardingDraftExists: false, profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Ai' }));
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Choose one AI provider' });
    if (provider === 'anthropic') await user.click(screen.getByLabelText('Anthropic / Claude'));
    await user.type(screen.getByLabelText('API key'), `${provider}-temporary-key`);
    await user.click(screen.getByRole('button', { name: 'Save credential' }));
    expect(screen.getByLabelText('Replacement API key')).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Verify provider' }));
    await user.click(screen.getByRole('button', { name: 'Discover models' }));
    await user.click(await screen.findByLabelText('Model'));
    await user.click(screen.getByRole('option', { name: new RegExp(`${provider} model`) }));
    await user.click(screen.getByRole('button', { name: 'Validate model' }));
    await user.click(await screen.findByRole('button', { name: new RegExp(`Confirm ${provider}-model`) }));
    expect(await screen.findByText('AI provider and model confirmed.')).toBeVisible();
    expect(backend.state.setup.aiModelConfigured).toBe(true);
  });

  it('AI-008 preserves an existing confirmed model during discovery outage', async () => {
    renderSetup(staged({ onboardingDraftExists: false, profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Ai' }), { discoveryUnavailable: true });
    expect(await screen.findByText(/Confirmed model:/)).toHaveTextContent('test-model');
    await userEvent.click(screen.getByRole('button', { name: 'Discover models' }));
    expect(await screen.findByText('Engineering Intake Gate is not ready for this operation yet.')).toBeVisible();
    expect(screen.getByText(/Confirmed model:/)).toHaveTextContent('test-model');
  });

  it('AI-010 manual model entry still requires confirmation and handles a stale candidate', async () => {
    renderSetup(staged({ aiModelConfigured: false, onboardingDraftExists: false, profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Ai' }), { aiSettings: { ...readyAiSettings, model: null, modelConfirmed: false, ready: false }, aiCredentials: { openai: verifiedCredential }, staleModelCandidate: true });
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Select a model' });
    await user.click(screen.getByRole('button', { name: 'Enter model ID manually' }));
    await user.type(screen.getByLabelText('Model ID'), 'manual-model-id');
    await user.click(screen.getByRole('button', { name: 'Validate model' }));
    await user.click(await screen.findByRole('button', { name: 'Confirm manual-model-id' }));
    expect(await screen.findByText('The information changed before this action completed. Refresh and try again.')).toBeVisible();
    expect(screen.getByLabelText('Model ID')).toHaveValue('manual-model-id');
  });

  it('UI-012 resumes persisted draft and supports keyboard-operable criterion add/remove', async () => {
    renderSetup(staged({ profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Profile' }), { draft: completeDraft });
    const user = userEvent.setup();
    expect(await screen.findByDisplayValue('https://example.test/intake-policy')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Add criterion' }));
    expect(screen.getByRole('button', { name: 'Remove criterion 2' })).toBeVisible();
    await user.tab();
    await user.click(screen.getByRole('button', { name: 'Remove criterion 2' }));
    expect(screen.queryByRole('button', { name: 'Remove criterion 2' })).not.toBeInTheDocument();
  });

  it('UI-012 refetches the authoritative draft after a stale revision conflict', async () => {
    const backend = renderSetup(staged({ profileDetailsComplete: false, policyDetailsComplete: false, lastVisitedStep: 'Profile' }), { draft: completeDraft });
    const user = userEvent.setup();
    const policyUrl = await screen.findByLabelText(/^Policy URL/);
    await user.clear(policyUrl);
    await user.type(policyUrl, 'https://example.test/new-policy');
    backend.state.draft = { ...backend.state.draft, revision: 4 };
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByText(/Setup changed in another session/)).toBeVisible();
    expect(screen.getByLabelText(/^Policy URL/)).toHaveValue('https://example.test/intake-policy');
  });

  it.each([
    ['GUID', '11111111-1111-1111-1111-111111111111'],
    ['URL', 'https://dev.azure.example/contoso/Engineering/_queries/query/11111111-1111-1111-1111-111111111111'],
  ])('ADO-QUERY-001 validates a saved-query %s, bounds preview to ten, and confirms', async (_kind, query) => {
    const setup = staged({ azureDevOpsSavedQueryConfirmed: false, lastVisitedStep: 'SavedQuery' });
    const backend = renderSetup(setup, { adoSettings: { ...readyAzureDevOpsSettings, savedQueryId: null, queryConfirmed: false, queryValidatedAtUtc: null }, queryPreviewCount: 12 });
    const user = userEvent.setup();
    await screen.findByRole('heading', { name: 'Confirm the governed saved query' });
    await user.type(screen.getByLabelText(/^Saved-query URL or GUID/), query);
    await user.click(screen.getByRole('button', { name: 'Validate query' }));
    const table = await screen.findByRole('table', { name: 'Saved query preview' });
    expect(within(table).getAllByRole('row')).toHaveLength(11);
    await user.click(screen.getByRole('button', { name: 'Confirm query' }));
    expect(await screen.findByText('Saved query confirmed.')).toBeVisible();
    expect(backend.state.setup.azureDevOpsSavedQueryConfirmed).toBe(true);
  });

  it('ADO-QUERY-002 treats a valid zero-result query as success', async () => {
    renderSetup(staged({ azureDevOpsSavedQueryConfirmed: false, lastVisitedStep: 'SavedQuery' }), { adoSettings: { ...readyAzureDevOpsSettings, savedQueryId: null, queryConfirmed: false, queryValidatedAtUtc: null }, queryTotalCount: 0, queryPreviewCount: 0 });
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Saved-query URL or GUID/), '11111111-1111-1111-1111-111111111111');
    await user.click(screen.getByRole('button', { name: 'Validate query' }));
    expect((await screen.findAllByText('Query is valid. No work items currently match.')).length).toBeGreaterThan(0);
  });

  it('ADO-QUERY-003 discards a stale query candidate and requires a new preview', async () => {
    renderSetup(staged({ azureDevOpsSavedQueryConfirmed: false, lastVisitedStep: 'SavedQuery' }), { adoSettings: { ...readyAzureDevOpsSettings, savedQueryId: null, queryConfirmed: false, queryValidatedAtUtc: null }, staleQueryCandidate: true });
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Saved-query URL or GUID/), '11111111-1111-1111-1111-111111111111');
    await user.click(screen.getByRole('button', { name: 'Validate query' }));
    await user.click(await screen.findByRole('button', { name: 'Confirm query' }));
    expect(await screen.findByText(/Validate the saved query again/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Confirm query' })).not.toBeInTheDocument();
  });

  it('ADO-005 distinguishes query validation failure from an empty successful query', async () => {
    renderSetup(staged({ azureDevOpsSavedQueryConfirmed: false, lastVisitedStep: 'SavedQuery' }), { adoSettings: { ...readyAzureDevOpsSettings, savedQueryId: null, queryConfirmed: false, queryValidatedAtUtc: null }, queryValidationFails: true });
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Saved-query URL or GUID/), '11111111-1111-1111-1111-111111111111');
    await user.click(screen.getByRole('button', { name: 'Validate query' }));
    expect(await screen.findByText('The request could not be completed safely.')).toBeVisible();
    expect(screen.queryByText('Query is valid. No work items currently match.')).not.toBeInTheDocument();
  });

  it.each([
    ['Manual only', false, ''],
    ['Hourly', true, '0 0 * * * *'],
    ['Daily', true, '0 0 2 * * *'],
    ['Weekly', true, '0 0 2 * * 1'],
  ] as const)('UI-012 maps schedule preset %s to backend draft values', async (choice, enabled, expression) => {
    const backend = renderSetup(staged({ lastVisitedStep: 'OptionalSchedule' }), { draft: completeDraft });
    const user = userEvent.setup();
    await user.click(await screen.findByLabelText('Frequency'));
    await user.click(screen.getByRole('option', { name: choice }));
    await user.click(screen.getByRole('button', { name: 'Save & continue' }));
    await screen.findByRole('heading', { name: 'Review authoritative setup' });
    const update = [...backend.calls].reverse().find((call) => call.path === '/api/setup/profile-draft' && call.method === 'PUT');
    const body = JSON.parse(update?.body ?? '{}') as { values?: { schedule?: { enabled?: boolean; expression?: string } } };
    expect(body.values?.schedule).toMatchObject({ enabled, expression });
  });

  it('UI-012 maps a custom six-field schedule without calculating browser-side run times', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'OptionalSchedule' }), { draft: completeDraft });
    const user = userEvent.setup();
    await user.click(await screen.findByLabelText('Frequency'));
    await user.click(screen.getByRole('option', { name: 'Custom' }));
    await user.type(await screen.findByLabelText(/^Six-field cron expression/), '0 30 6 * * 1-5');
    await user.click(screen.getByRole('button', { name: 'Save & continue' }));
    await screen.findByRole('heading', { name: 'Review authoritative setup' });
    const update = [...backend.calls].reverse().find((call) => call.path === '/api/setup/profile-draft' && call.method === 'PUT');
    const body = JSON.parse(update?.body ?? '{}') as { values?: { schedule?: { enabled?: boolean; expression?: string } } };
    expect(body.values?.schedule).toMatchObject({ enabled: true, expression: '0 30 6 * * 1-5' });
    expect(screen.queryByText(/next run/i)).not.toBeInTheDocument();
  });

  it('UI-012 leaves invalid schedule/timezone state in the wizard for authoritative backend correction', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'OptionalSchedule' }), { draft: completeDraft, draftValidationFails: true });
    const user = userEvent.setup();
    const timezone = await screen.findByLabelText(/^Timezone/);
    await user.clear(timezone);
    await user.type(timezone, 'Not/A-Timezone');
    await user.click(screen.getByRole('button', { name: 'Save & continue' }));
    expect(await screen.findByText('The schedule expression or timezone is invalid.')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Optional schedule' })).toBeVisible();
    expect(backend.calls.some((call) => call.path === '/api/setup/finalize')).toBe(false);
  });

  it('UI-013 reviews safe authoritative state and sends only the expected draft revision before Home', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft, profile: { ...profileState, exists: false } });
    expect(await screen.findByRole('heading', { name: 'Review authoritative setup' })).toBeVisible();
    expect(screen.queryByText(/PAT|API key|ciphertext|encryption key/i)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Finish setup and activate Dry Run' }));
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    const call = backend.calls.find((item) => item.path === '/api/setup/finalize');
    expect(call?.body).toBe('{"expectedDraftRevision":3}');
  });

  it('UI-013 remains in setup on activation failure and does not retry automatically', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft, finalizeActivationFails: true });
    await screen.findByRole('heading', { name: 'Review authoritative setup' });
    await userEvent.click(screen.getByRole('button', { name: 'Finish setup and activate Dry Run' }));
    expect(await screen.findByText('The configuration could not be activated safely.')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Review authoritative setup' })).toBeVisible();
    expect(backend.calls.filter((item) => item.path === '/api/setup/finalize')).toHaveLength(1);
  });

  it('UI-013 remains in Review on final validation failure and does not retry automatically', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft, finalizeValidationFails: true });
    await screen.findByRole('heading', { name: 'Review authoritative setup' });
    await userEvent.click(screen.getByRole('button', { name: 'Finish setup and activate Dry Run' }));
    expect(await screen.findByText('Some information was not accepted. Review the form and try again.')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Review authoritative setup' })).toBeVisible();
    expect(backend.calls.filter((item) => item.path === '/api/setup/finalize')).toHaveLength(1);
  });

  it('UI-013 refetches authoritative state after a stale finalization revision', async () => {
    const backend = renderSetup(staged({ lastVisitedStep: 'ReviewFinish' }), { draft: completeDraft, finalizeConflict: true });
    await screen.findByRole('heading', { name: 'Review authoritative setup' });
    await userEvent.click(screen.getByRole('button', { name: 'Finish setup and activate Dry Run' }));
    expect(await screen.findByText(/latest authoritative state is loaded/)).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Review authoritative setup' })).toBeVisible();
    expect(backend.calls.filter((item) => item.path === '/api/setup/finalize')).toHaveLength(1);
  });
});
