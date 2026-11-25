import { afterEach, describe, expect, it, vi } from 'vitest';
import { normalizeMemo } from '../../src/mobile/src/domain/tags';
import {
  MemoryApi, StaleCloudSessionError, type CloudSessionBoundary
} from '../../src/mobile/src/services/memoryApi';

const cfg = {
  issuer: 'http://localhost:8081/realms/tao',
  clientId: 'tao-web',
  apiBaseUrl: 'http://localhost:8080'
};
afterEach(() => vi.unstubAllGlobals());

function boundary() {
  let currentEpoch = 1;
  const unauthorized = vi.fn();
  const session: CloudSessionBoundary = {
    currentEpoch: () => currentEpoch,
    unauthorized
  };
  return { session, unauthorized, invalidate: () => { currentEpoch++; } };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

describe('in-flight signed web session lifecycle #172', () => {
  it('never parses or delivers late successful POST after signout', async () => {
    const gate = boundary();
    const pending = deferred<Response>();
    const dispatched = deferred<void>();
    const json = vi.fn(async () => ({ id: 'private-former-user' }));
    vi.stubGlobal('fetch', vi.fn(() => {
      dispatched.resolve();
      return pending.promise;
    }));
    const api = new MemoryApi(cfg, async () => 'synthetic-signed-bearer', gate.session);
    const sent = api.save(normalizeMemo('Riunione lavoro', '2026-11-22T10:00:00Z'));
    await dispatched.promise; // the POST has actually left the client
    gate.invalidate(); // user logout before response delivery
    pending.resolve({ ok: true, status: 201, json } as unknown as Response);
    await expect(sent).rejects.toBeInstanceOf(StaleCloudSessionError);
    expect(json).not.toHaveBeenCalled();
    expect(gate.unauthorized).not.toHaveBeenCalled();
  });

  it('never returns private data if JSON deserialization finishes after logout', async () => {
    const gate = boundary();
    const contents = deferred<unknown>();
    const enteredBody = deferred<void>();
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true, status: 200, json: async () => {
        enteredBody.resolve();
        return contents.promise;
      }
    })));
    const api = new MemoryApi(cfg, async () => 'signed', gate.session);
    const query = api.recent();
    await enteredBody.promise;
    gate.invalidate();
    contents.resolve([{ id: 'former-private-note' }]);
    await expect(query).rejects.toBeInstanceOf(StaleCloudSessionError);
  });

  it('never reads an export body from a previous signed-in session', async () => {
    const gate = boundary();
    const pending = deferred<Response>();
    const dispatched = deferred<void>();
    const blob = vi.fn(async () => new Blob(['private-json']));
    vi.stubGlobal('fetch', vi.fn(() => {
      dispatched.resolve();
      return pending.promise;
    }));
    const api = new MemoryApi(cfg, async () => 'signed', gate.session);
    const exported = api.exportPersonalData();
    await dispatched.promise; // actual GET issued before signout
    gate.invalidate();
    pending.resolve({ ok: true, status: 200, blob } as unknown as Response);
    await expect(exported).rejects.toBeInstanceOf(StaleCloudSessionError);
    expect(blob).not.toHaveBeenCalled();
  });

  it('never delivers a private export if logout occurs while reading its body', async () => {
    const gate = boundary();
    const contents = deferred<Blob>();
    const started = deferred<void>();
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true, status: 200, blob: async () => {
        started.resolve();
        return contents.promise;
      }
    })));
    const api = new MemoryApi(cfg, async () => 'signed', gate.session);
    const exported = api.exportPersonalData();
    await started.promise;
    gate.invalidate();
    contents.resolve(new Blob(['previous-user-private']));
    await expect(exported).rejects.toBeInstanceOf(StaleCloudSessionError);
  });

  it('does not misreport an old network error as a current session failure', async () => {
    const gate = boundary();
    const pending = deferred<Response>();
    const dispatched = deferred<void>();
    vi.stubGlobal('fetch', vi.fn(() => {
      dispatched.resolve();
      return pending.promise;
    }));
    const api = new MemoryApi(cfg, async () => 'old-signed', gate.session);
    const query = api.recent();
    await dispatched.promise;
    gate.invalidate();
    pending.reject(new Error('old request network failed'));
    await expect(query).rejects.toBeInstanceOf(StaleCloudSessionError);
  });

  it('preserves successful new-session captures and current 403 denials', async () => {
    const gate = boundary();
    const entry = normalizeMemo('Riunione sintetica', '2026-11-22T10:00:00Z');
    const response = { id: 'current-accepted', source: 'manual' };
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true, status: 201, json: async () => response
    })));
    const api = new MemoryApi(cfg, async () => 'signed-current', gate.session);
    expect(await api.save(entry)).toMatchObject(response);
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 403 })));
    await expect(api.save(entry)).rejects.toMatchObject({ status: 403 });
    expect(gate.unauthorized).not.toHaveBeenCalled();
  });
});
