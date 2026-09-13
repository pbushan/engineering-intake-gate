import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { apiClient } from '../api/client';
import { adminUser, completeSetup, incompleteSetup, installMockBackend, viewerUser } from './mockBackend';

const renderAt = (path = '/') => {
  window.history.replaceState({}, '', path);
  return render(<App />);
};

describe('Phase 4A application lifecycle', () => {
  beforeEach(() => {
    apiClient.resetCsrf();
    localStorage.clear();
    sessionStorage.clear();
    vi.unstubAllGlobals();
  });

  it('AUTH-004 renders login and shows a safe generic failure', async () => {
    installMockBackend({ loginSucceeds: false });
    renderAt('/login');
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/^Username/), 'admin');
    await user.type(screen.getByLabelText(/^Password/), 'incorrect-password');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in failed');
    expect(screen.queryByText('account-specific unsafe detail')).not.toBeInTheDocument();
  });

  it('AUTH-004 successful login reaches the authenticated shell', async () => {
    installMockBackend({ setup: completeSetup });
    renderAt('/login');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Username/), 'admin');
    await user.type(screen.getByLabelText(/^Password/), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    expect(screen.getAllByText('Controlled Dry Run', { selector: '.MuiChip-label' })).not.toHaveLength(0);
  });

  it('AUTH-001 shows bootstrap only while available and completes into setup', async () => {
    installMockBackend({ bootstrapAvailable: true, setup: incompleteSetup });
    renderAt('/login');
    expect(await screen.findByRole('heading', { name: 'Create the first administrator' })).toBeVisible();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/^Username/), 'first-admin');
    await user.type(screen.getByLabelText('Display name (optional)'), 'First Admin');
    await user.type(screen.getByLabelText(/^Password/), 'bootstrap-password');
    await user.click(screen.getByRole('button', { name: 'Create administrator' }));
    expect(await screen.findByRole('heading', { name: 'Set up Engineering Intake Gate' })).toBeVisible();
  });

  it('AUTH-001 redirects a closed bootstrap route to login', async () => {
    installMockBackend({ bootstrapAvailable: false });
    renderAt('/bootstrap');
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });

  it('AUTH-001 handles a bootstrap race as a safe conflict', async () => {
    installMockBackend({ bootstrapAvailable: true, bootstrapConflicts: true, setup: incompleteSetup });
    renderAt('/bootstrap');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Username/), 'first-admin');
    await user.type(screen.getByLabelText(/^Password/), 'bootstrap-password');
    await user.click(screen.getByRole('button', { name: 'Create administrator' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Another administrator completed bootstrap first');
    expect(screen.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });

  it('AUTH-003 restores a backend session after refresh and logs out', async () => {
    const backend = installMockBackend({ user: adminUser });
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
    expect(backend.state.user).toBeNull();
  });

  it('AUTH-003 clears protected UI on a later 401', async () => {
    const backend = installMockBackend({ user: adminUser });
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    backend.state.user = null;
    await expect(apiClient.getProfile()).rejects.toMatchObject({ kind: 'unauthenticated' });
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });

  it('AUTH-005 keeps Viewer presentation read-only', async () => {
    installMockBackend({ user: viewerUser, setup: completeSetup });
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    expect(screen.queryByText('Analyze Ticket')).not.toBeInTheDocument();
    expect(screen.queryByText(/Profile \/ Configuration/)).not.toBeInTheDocument();
    expect(screen.getAllByText('viewer')).not.toHaveLength(0);
  });

  it('AUTH-005 shows bounded administrator shell destinations', async () => {
    installMockBackend({ user: adminUser, setup: completeSetup });
    renderAt('/');
    expect(await screen.findByText('Analyze Ticket')).toBeVisible();
    expect(screen.getByText('Profile / Configuration (Beta)')).toBeVisible();
    expect(screen.getAllByText('admin').length).toBeGreaterThan(0);
  });

  it('SETUP routes incomplete Admin and Viewer experiences separately', async () => {
    const adminView = installMockBackend({ user: adminUser, setup: incompleteSetup });
    const { unmount } = renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Set up Engineering Intake Gate' })).toBeVisible();
    unmount();
    apiClient.resetCsrf();
    adminView.state.user = viewerUser;
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'This installation is not ready yet' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Setup' })).not.toBeInTheDocument();
  });

  it('SETUP routes complete installations to the operational Home summary', async () => {
    installMockBackend({ user: adminUser, setup: completeSetup });
    renderAt('/setup');
    expect(await screen.findByRole('heading', { name: 'Engineering Intake Gate' })).toBeVisible();
    expect(await screen.findByRole('heading', { name: 'Last 30 days' })).toBeVisible();
  });

  it('AUTH-003 shows a recoverable backend unavailable state', async () => {
    const backend = installMockBackend({ unavailable: true });
    renderAt('/');
    expect(await screen.findByRole('heading', { name: 'Backend unavailable' })).toBeVisible();
    backend.state.unavailable = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });

  it('SEC-001 never writes authentication or token material to persistent browser storage', async () => {
    installMockBackend({ setup: completeSetup });
    const localSpy = vi.spyOn(Storage.prototype, 'setItem');
    renderAt('/login');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/^Username/), 'admin');
    await user.type(screen.getByLabelText(/^Password/), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByRole('heading', { name: 'Engineering Intake Gate' });
    expect(localSpy).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('AUTH-003 does not flash protected content before session resolution', async () => {
    let resolveSession: ((response: Response) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      if (String(input).endsWith('/api/auth/session')) return new Promise<Response>((resolve) => { resolveSession = resolve; });
      return Promise.resolve(new Response(JSON.stringify({ available: false }), { headers: { 'content-type': 'application/json' } }));
    }));
    renderAt('/home');
    expect(screen.getByRole('heading', { name: 'Opening Engineering Intake Gate' })).toBeVisible();
    expect(screen.queryByText('Operational overview')).not.toBeInTheDocument();
    resolveSession?.(new Response(JSON.stringify({ error: 'Unauthenticated', message: 'Authentication is required.' }), { status: 401, headers: { 'content-type': 'application/json' } }));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Welcome back' })).toBeVisible());
  });
});
