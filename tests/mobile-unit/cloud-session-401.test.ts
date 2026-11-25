import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryApi, type CloudSessionBoundary } from '../../src/mobile/src/services/memoryApi';

const cfg = {
  issuer: 'http://localhost:8081/realms/tao',
  clientId: 'tao-web',
  apiBaseUrl: 'http://localhost:8080'
};
afterEach(() => vi.unstubAllGlobals());

function boundary() {
  let epoch = 0;
  const clear = vi.fn(() => { epoch++; });
  const session: CloudSessionBoundary = {
    currentEpoch: () => epoch,
    unauthorized: clear
  };
  return { session, clear, advance: () => { epoch++; } };
}

describe('cloud bearer 401 session boundary', () => {
  it('clears the current session on 401 GET, without trusting request headers', async () => {
    const gate = boundary();
    const fetcher = vi.fn().mockResolvedValue({ ok: false, status: 401 });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => 'opaque-signed-access-token', gate.session);
    await expect(api.recent()).rejects.toMatchObject({ status: 401 });
    expect(gate.clear).toHaveBeenCalledTimes(1);
    expect(fetcher).toHaveBeenCalledWith('http://localhost:8080/api/v1/memories?limit=30',
      expect.objectContaining({
        credentials: 'omit', redirect: 'error', cache: 'no-store',
        headers: expect.objectContaining({
          Authorization: 'Bearer opaque-signed-access-token'
        })
      }));
  });

  it('applies the same 401 boundary to sensitive personal-data exports', async () => {
    const gate = boundary();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));
    const api = new MemoryApi(cfg, async () => 'synthetic-bearer', gate.session);
    await expect(api.exportPersonalData()).rejects.toMatchObject({ status: 401 });
    expect(gate.clear).toHaveBeenCalledOnce();
  });

  it('does not log out on 403, 409, 429, 503 or a temporary network failure', async () => {
    const gate = boundary();
    const api = new MemoryApi(cfg, async () => 'signed-actor', gate.session);
    for (const status of [403, 409, 429, 503]) {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status }));
      await expect(api.recent()).rejects.toMatchObject({ status });
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 409 }));
    await expect(api.recent()).rejects.toThrow('Nota originale non sovrascritta.');
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));
    await expect(api.recent()).rejects.toMatchObject({ status: 0 });
    expect(gate.clear).not.toHaveBeenCalled();
  });

  it('rejects a late 401 from an old account without clearing a new login', async () => {
    const gate = boundary();
    let respond!: (response: Response) => void;
    let requested!: () => void;
    const requestMade = new Promise<void>(resolve => { requested = resolve; });
    vi.stubGlobal('fetch', vi.fn(() => {
      requested();
      return new Promise<Response>(resolve => { respond = resolve; });
    }));
    const api = new MemoryApi(cfg, async () => 'old-account-bearer', gate.session);
    const inFlight = api.recent();
    await requestMade;
    gate.advance(); // logout and subsequent login own a different epoch
    respond({ ok: false, status: 401 } as Response);
    await expect(inFlight).rejects.toMatchObject({ status: 401 });
    expect(gate.clear).not.toHaveBeenCalled();
  });

  it('never dispatches a request if the token getter resolves after logout', async () => {
    const gate = boundary();
    let resolveToken!: (value: string) => void;
    const tokenPromise = new Promise<string>(resolve => { resolveToken = resolve; });
    const fetcher = vi.fn();
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, () => tokenPromise, gate.session);
    const inFlight = api.recent();
    gate.advance();
    resolveToken('expired-previous-account-bearer');
    await expect(inFlight).rejects.toMatchObject({ status: 401 });
    expect(fetcher).not.toHaveBeenCalled();
    expect(gate.clear).not.toHaveBeenCalled();
  });

  it('invalidates the view when the in-memory OIDC token cannot be obtained', async () => {
    const gate = boundary();
    const fetcher = vi.fn();
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => {
      throw new Error('OIDC token expired');
    }, gate.session);
    await expect(api.recent()).rejects.toThrow('OIDC token expired');
    expect(gate.clear).toHaveBeenCalledOnce();
    expect(fetcher).not.toHaveBeenCalled();
  });
});
