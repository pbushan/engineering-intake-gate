import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import { adminUser, completeDraft, completeSetup, editableProfileState, installMockBackend, viewerUser } from './mockBackend';

const renderAt = (path: string) => {
  window.history.replaceState({}, '', path);
  return render(<App />);
};

async function openProfileStep() {
  expect(await screen.findByRole('heading', { name: 'Edit profile and configuration' })).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Review configuration' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  expect(await screen.findByRole('heading', { name: 'Profile basics' })).toBeVisible();
}

async function openAiStep() {
  expect(await screen.findByRole('heading', { name: 'Edit profile and configuration' })).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Review configuration' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  expect(await screen.findByText('Select a model')).toBeVisible();
}

async function openSavedQueryStep() {
  await openProfileStep();
  await userEvent.click(screen.getByRole('button', { name: 'Save & continue' }));
  expect(await screen.findByText('Confirm the governed saved query')).toBeVisible();
}

async function openReviewFromProfile() {
  await userEvent.click(screen.getByRole('button', { name: 'Save & continue' }));
  expect(await screen.findByText('Confirm the governed saved query')).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  expect(await screen.findByText('Optional schedule')).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Save & continue' }));
  expect(await screen.findByText('Review profile changes')).toBeVisible();
}

describe('Profile / Configuration editing', () => {
  beforeEach(() => {
    apiClient.resetCsrf();
    vi.unstubAllGlobals();
  });

  it('is Admin-only and redirects a Viewer away from the route', async () => {
    const backend = installMockBackend({ user: viewerUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Edit profile and configuration' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Profile \/ Configuration/)).not.toBeInTheDocument();
    expect(backend.calls.some((call) => call.path === '/api/setup/profile-draft')).toBe(false);
  });

  it('pre-populates the reusable wizard without rendering credential values', async () => {
    installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    await openProfileStep();

    expect(screen.getByLabelText('Profile version (optional)')).toHaveValue('1.0');
    expect(screen.getByLabelText(/^Policy URL/)).toHaveValue('https://example.test/intake-policy');
    expect(screen.getByLabelText(/^Engineering Ready tag/)).toHaveValue('Engineering Ready');
    expect(screen.getByLabelText(/^Intake Incomplete tag/)).toHaveValue('Intake Incomplete');
    expect(screen.getByLabelText(/^Criterion ID/)).toHaveValue('reproduction');
    expect(screen.getByLabelText(/^Evaluation guidance/)).toHaveValue('Look for bounded reproduction evidence.');
    expect(screen.getByLabelText(/^Exclusion ID/)).toHaveValue('excluded-state');
    expect(screen.getByLabelText(/^Values \(one per line\)/)).toHaveValue('Removed\nClosed');
    expect(screen.getByLabelText(/^Pricing model/)).toHaveValue('test-model');
    expect(screen.getByLabelText(/^Input per million tokens/)).toHaveValue(2.5);
    expect(document.body.textContent).not.toContain('correct-horse-battery');
    expect(document.body.textContent).not.toContain('ciphertext');
  }, 15_000);

  it('exports a portable JSON file and requires confirmation before import replacement', async () => {
    const backend = installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    const createObjectURL = vi.fn(() => 'blob:profile-export');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    renderAt('/configuration');
    expect(await screen.findByRole('button', { name: 'Import Profile' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Export Profile' })).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Export Profile' }));
    await waitFor(() => expect(createObjectURL).toHaveBeenCalledOnce());
    expect(anchorClick).toHaveBeenCalledOnce();
    expect(await screen.findByText('Profile & Policy configuration exported.')).toBeVisible();

    const exported = {
      format: 'engineering-intake-gate-profile', version: 1, exportedAt: '2026-09-18T12:00:00Z',
      profile: { ...completeDraft.values, policy: undefined, audit: { retentionDays: 120, evidenceRetentionDays: 30, maximumSelectedVideoScreenshots: 6 } },
      policy: completeDraft.values!.policy,
    };
    const input = screen.getByLabelText('Choose Profile JSON file');
    await userEvent.upload(input, new File([JSON.stringify(exported)], 'profile.json', { type: 'application/json' }));
    expect(await screen.findByRole('dialog', { name: 'Replace Profile & Policy configuration?' })).toBeVisible();
    expect(backend.calls.some((call) => call.path.startsWith('/api/profile/import?'))).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(backend.state.profile.configurationRevision).toBe('sha256:deterministic');

    await userEvent.upload(input, new File([JSON.stringify(exported)], 'profile.json', { type: 'application/json' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Import' }));
    expect(await screen.findByText('Profile & Policy configuration imported successfully.')).toBeVisible();
    expect(backend.state.profile.configurationRevision).toBe('sha256:imported');
    expect(backend.state.profile.audit?.retentionDays).toBe(120);
  }, 15_000);

  it('rejects malformed files and imports incomplete profiles into validation', async () => {
    installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    const input = await screen.findByLabelText('Choose Profile JSON file');
    await userEvent.upload(input, new File(['{not json'], 'broken.json', { type: 'application/json' }));
    expect(await screen.findByText('The selected file is not a valid Engineering Intake Gate profile.')).toBeVisible();

    const incomplete = {
      format: 'engineering-intake-gate-profile', version: 1, exportedAt: '2026-09-18T12:00:00Z',
      profile: { ...completeDraft.values, policy: undefined, policyUrl: null },
      policy: { id: null, version: null, criteria: null },
    };
    await userEvent.upload(input, new File([JSON.stringify(incomplete)], 'incomplete.json', { type: 'application/json' }));
    expect(await screen.findByText(/This profile is incomplete/)).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(await screen.findByText('Profile imported. Complete the missing Profile & Policy information before activation.')).toBeVisible();
    expect(screen.getByText('Policy URL')).toBeVisible();
  }, 15_000);

  it('saves edited criteria with the current profile revision and protects unsaved changes', async () => {
    const backend = installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    await openProfileStep();

    const guidance = screen.getByLabelText(/^Evaluation guidance/);
    await userEvent.clear(guidance);
    await userEvent.type(guidance, 'Require exact reproduction steps and observed behavior.');
    const unload = new Event('beforeunload', { cancelable: true });
    fireEvent(window, unload);
    expect(unload.defaultPrevented).toBe(true);

    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    await openReviewFromProfile();
    expect(backend.calls.some((call) => call.path === '/api/profile' && call.method === 'PUT')).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Save and activate changes' }));
    await waitFor(() => expect(backend.calls.some((call) => call.path === '/api/profile' && call.method === 'PUT')).toBe(true));
    const update = [...backend.calls].reverse().find((call) => call.path === '/api/profile' && call.method === 'PUT');
    expect(JSON.parse(update!.body!)).toMatchObject({
      expectedRevision: 'sha256:deterministic',
      profile: { policy: { criteria: [{ evaluationGuidance: 'Require exact reproduction steps and observed behavior.' }] } },
    });
    expect(update!.body).not.toContain('credential');
    expect(backend.state.profile.configurationRevision).toBe('sha256:updated');
  }, 15_000);

  it('requires a changed saved query to be validated and confirmed against the fixed ADO boundary', async () => {
    const backend = installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    await openSavedQueryStep();

    const query = screen.getByLabelText(/^Saved-query URL or GUID/);
    await userEvent.clear(query);
    await userEvent.type(query, '22222222-2222-2222-2222-222222222222');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('Validate and confirm the saved query before continuing.')).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Validate query' }));
    expect(await screen.findByRole('table', { name: 'Saved query preview' })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Confirm query' }));
    expect(await screen.findByText('Saved query confirmed.')).toBeVisible();
    expect(query).toHaveValue('22222222-2222-2222-2222-222222222222');

    const validation = [...backend.calls].reverse().find((call) => call.path === '/api/ado/query-candidates/validate');
    expect(JSON.parse(validation!.body!)).toEqual({
      organizationUrl: 'https://dev.azure.example/contoso',
      project: 'Engineering',
      savedQuery: '22222222-2222-2222-2222-222222222222',
    });
  }, 15_000);

  it('requires AI credential verification and model confirmation after replacement', async () => {
    const backend = installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState });
    renderAt('/configuration');
    await openAiStep();

    await userEvent.type(screen.getByLabelText('Replacement API key'), 'temporary-test-secret');
    await userEvent.click(screen.getByRole('button', { name: 'Replace credential' }));
    expect(await screen.findByText('OpenAI credential configured.')).toBeVisible();
    expect(screen.getByLabelText('Replacement API key')).toHaveValue('');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('Verify the selected provider and validate and confirm the selected model before continuing.')).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Verify provider' }));
    expect(await screen.findByText('OpenAI credential verified.')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Enter model ID manually' }));
    const model = screen.getByLabelText('Model ID');
    await userEvent.clear(model);
    await userEvent.type(model, 'new-model');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('Verify the selected provider and validate and confirm the selected model before continuing.')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Validate model' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm new-model' }));
    expect(await screen.findByText('AI provider and model confirmed.')).toBeVisible();
    expect(backend.calls.some((call) => call.path === '/api/ai/model-candidates/confirm')).toBe(true);
    expect(document.body.textContent).not.toContain('temporary-test-secret');
  }, 15_000);

  it('shows actionable validation and reloads the latest profile after a conflict', async () => {
    installMockBackend({ user: adminUser, setup: completeSetup, profile: editableProfileState, profileUpdateConflict: true });
    renderAt('/configuration');
    await openProfileStep();

    const guidance = screen.getByLabelText(/^Evaluation guidance/);
    await userEvent.clear(guidance);
    await userEvent.type(guidance, 'A conflicting edit.');
    await openReviewFromProfile();
    await userEvent.click(screen.getByRole('button', { name: 'Save and activate changes' }));
    expect(await screen.findByText('The profile or integration state changed in another session. The latest persisted values are loaded; review them before retrying.')).toBeVisible();
    expect(screen.getByText('Review profile changes')).toBeVisible();
    expect(screen.getByText('The profile changed; refresh and retry.')).toBeVisible();
  }, 15_000);
});
