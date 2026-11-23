import { describe, expect, it, vi, afterEach } from 'vitest';
import { hasOidcCallback, isAllowedPublicEndpoint, resolveWebMode } from '../../src/mobile/src/services/config';
import { CloudApiError, MemoryApi } from '../../src/mobile/src/services/memoryApi';

const cloud = { issuer: 'http://localhost:8081/realms/omnia', clientId: 'omnia-web', apiBaseUrl: 'http://localhost:8080' };
afterEach(() => vi.unstubAllGlobals());

describe('OIDC public client config', () => {
  it('keeps public Pages in explicitly local demo when not configured', () =>
    expect(resolveWebMode({})).toEqual({ kind: 'demo' }));
  it('never silently falls back to demo after a partial or unsafe config', () => {
    expect(resolveWebMode({ issuer: cloud.issuer }).kind).toBe('invalid');
    expect(resolveWebMode({ ...cloud, apiBaseUrl: 'http://evil.example' }).kind).toBe('invalid');
    expect(resolveWebMode({ ...cloud, issuer: 'https://user:pass@id.example' }).kind).toBe('invalid');
    expect(resolveWebMode(cloud).kind).toBe('cloud');
  });
  it('requires HTTPS except for explicit loopback development', () => {
    expect(isAllowedPublicEndpoint('https://identity.example/realms/omnia')).toBe(true);
    expect(isAllowedPublicEndpoint('http://127.0.0.1:8080')).toBe(true);
    expect(isAllowedPublicEndpoint('http://localhost:8080')).toBe(true);
    expect(isAllowedPublicEndpoint('http://identity.example')).toBe(false);
    expect(isAllowedPublicEndpoint('javascript:alert(1)')).toBe(false);
  });
  it('recognizes authorization callbacks without mistaking regular URLs for them', () => {
    expect(hasOidcCallback('?code=abc&state=def')).toBe(true);
    expect(hasOidcCallback('?error=access_denied')).toBe(true);
    expect(hasOidcCallback('?utm_source=mail')).toBe(false);
  });
});

describe('privacy export and erasure', () => {
  it('exports a blob only with bearer token and without credentials or redirects', async () => {
    const fakeBlob = new Blob(['{"schemaVersion":1}'], { type: 'application/json' });
    const request = vi.fn().mockResolvedValue({
      ok: true, status: 200, blob: async () => fakeBlob
    });
    vi.stubGlobal('fetch', request);
    const api = new MemoryApi(cloud, async () => 'synthetic-access-token');
    expect(await api.exportPersonalData()).toBe(fakeBlob);
    expect(request).toHaveBeenCalledWith('http://localhost:8080/api/v1/privacy/export',
      expect.objectContaining({
        credentials: 'omit', redirect: 'error', cache: 'no-store',
        headers: expect.objectContaining({ Authorization: 'Bearer synthetic-access-token' })
      }));
  });

  it('requires explicit erasure confirmation and refuses to fake a success', async () => {
    const request = vi.fn().mockResolvedValue({
      ok: false, status: 400
    });
    vi.stubGlobal('fetch', request);
    const api = new MemoryApi(cloud, async () => 'synthetic-access-token');
    await expect(api.erasePersonalData('wrong')).rejects.toMatchObject({ status: 400 });
    expect(request).toHaveBeenCalledWith('http://localhost:8080/api/v1/privacy/erase',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ confirmation: 'wrong' }),
        credentials: 'omit', redirect: 'error'
      }));
  });
});

describe('cloud API', () => {
  it('sends bearer token and scope only, never a client-controlled tenant ID', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => [] });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cloud, async () => 'synthetic-access-token');
    await api.listConsents();
    expect(fetcher).toHaveBeenCalledWith('http://localhost:8080/api/v1/consents',
      expect.objectContaining({ credentials: 'omit', redirect: 'error',
        headers: expect.objectContaining({ Authorization: 'Bearer synthetic-access-token' }) }));
    expect(JSON.stringify(fetcher.mock.calls)).not.toContain('tenant_id');
  });
  it('requests bounded consent history with opaque encoded cursor and signed actor only', async () => {
    const reply = { items: [], nextCursor: null };
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => reply });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cloud, async () => 'synthetic-access-token');
    expect(await api.consentPage('c1.abc+/=')).toEqual(reply);
    expect(fetcher).toHaveBeenCalledWith(
      'http://localhost:8080/api/v1/consents/page?limit=25&before=c1.abc%2B%2F%3D',
      expect.objectContaining({ method: 'GET', credentials: 'omit',
        cache: 'no-store', redirect: 'error',
        headers: expect.objectContaining({ Authorization: 'Bearer synthetic-access-token' }) }));
    expect(JSON.stringify(fetcher.mock.calls)).not.toContain('tenant_id');
  });
  it('does not fake success when network is unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));
    const api = new MemoryApi(cloud, async () => 'synthetic-token');
    await expect(api.recent()).rejects.toMatchObject({ status: 0 });
  });
  it('reports consent denied distinctly', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 403 }));
    const api = new MemoryApi(cloud, async () => 'synthetic-token');
    await expect(api.recent()).rejects.toBeInstanceOf(CloudApiError);
    await expect(api.recent()).rejects.toMatchObject({ status: 403 });
  });
});
