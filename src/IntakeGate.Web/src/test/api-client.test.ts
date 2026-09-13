import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/api-error';
import { ApiClient } from '../api/client';

const json = (body: unknown, status = 200): Response => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

describe('API client security and errors', () => {
  beforeEach(() => vi.unstubAllGlobals());

  it('AUTH-008 attaches the antiforgery header to mutations and keeps the token in memory', async () => {
    const calls: RequestInit[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      calls.push(init);
      return String(input).endsWith('/csrf') ? json({ token: 'memory-only-token' }) : new Response(null, { status: 204 });
    }));
    const client = new ApiClient();
    await client.logout();
    expect(new Headers(calls[1]?.headers).get('X-CSRF-TOKEN')).toBe('memory-only-token');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('AUTH-008 refreshes once after a stale antiforgery token', async () => {
    let csrfCount = 0;
    let mutationCount = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).endsWith('/csrf')) return json({ token: `token-${++csrfCount}` });
      mutationCount += 1;
      return mutationCount === 1 ? json({ error: 'InvalidAntiforgeryToken', message: 'Invalid.' }, 400) : new Response(null, { status: 204 });
    }));
    await new ApiClient().logout();
    expect(csrfCount).toBe(2);
    expect(mutationCount).toBe(2);
  });

  it.each([
    [400, 'validation'], [403, 'forbidden'], [404, 'notFound'], [409, 'conflict'], [503, 'notReady'],
  ] as const)('maps HTTP %i to %s without exposing exceptions', async (status, kind) => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ error: 'SafeCode', message: 'Safe explanation.' }, status)));
    await expect(new ApiClient().getProfile()).rejects.toMatchObject({ kind, status, code: 'SafeCode' });
  });

  it('maps backend unavailability to a recoverable network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('socket details'); }));
    await expect(new ApiClient().getProfile()).rejects.toEqual(ApiError.network());
  });

  it('preserves structured validation fields and discards malformed entries', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({
      error: 'ValidationFailed',
      message: 'Some fields need attention.',
      fieldErrors: { policyUrl: ['Enter a valid policy URL.'], malformed: 'not-an-array' },
      sectionErrors: { 'policy.criteria': ['Add at least one intake criterion.'] },
    }, 400)));
    await expect(new ApiClient().getProfile()).rejects.toMatchObject({
      kind: 'validation',
      fieldErrors: { policyUrl: ['Enter a valid policy URL.'] },
      sectionErrors: { 'policy.criteria': ['Add at least one intake criterion.'] },
    });
  });

  it('keeps provider outages distinct from credential validation', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({
      error: 'AiProviderUnavailable',
      message: 'The AI provider is temporarily unavailable. Try again later; the credential was not marked invalid.',
    }, 503)));
    await expect(new ApiClient().getProfile()).rejects.toMatchObject({
      kind: 'external',
      fieldErrors: {},
    });
  });

  it('treats a provider-rejected credential as user-correctable when the backend identifies its field', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({
      error: 'AiAuthenticationFailed',
      message: 'The AI provider rejected the credential.',
      fieldErrors: { 'ai.credential': ['Replace or correct the stored credential, then verify again.'] },
    }, 502)));
    await expect(new ApiClient().getProfile()).rejects.toMatchObject({
      kind: 'validation',
      fieldErrors: { 'ai.credential': ['Replace or correct the stored credential, then verify again.'] },
    });
  });
});
