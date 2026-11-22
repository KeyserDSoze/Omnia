import { describe, expect, it, vi } from 'vitest';
import { CloudViewEpoch, appendUniqueById } from '../../src/mobile/src/services/cloudViewEpoch';
import type { ConsentDto, RemoteMemory, MemoryPageDto } from '../../src/mobile/src/services/memoryApi';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const sampleConsent = (id: string): ConsentDto => ({
  id, source: 'manual', purpose: 'memory.capture', policyVersion: 'privacy-v1',
  grantedAt: '2026-11-22T10:00:00Z', expiresAt: '2026-12-01T10:00:00Z',
  revokedAt: null, active: true
});
const sampleNote = (id: string): RemoteMemory => ({
  id, source: 'manual', summary: 'Solo il mio account', tags: [],
  occurredAt: '2026-11-22T10:00:00Z', hasEmbedding: false
});
const paged = (items: RemoteMemory[], nextCursor: string | null = null): MemoryPageDto =>
  ({ items, nextCursor });

describe('session-scoped cloud view refresh - no stale personal data', () => {
  it('ignores an authenticated response delivered after logout invalidated the view', async () => {
    const consents = deferred<ConsentDto[]>();
    const memories = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const pending = view.refresh({
      listConsents: () => consents.promise,
      page: () => memories.promise
    }, apply);

    view.invalidate(); // sign-out starts BEFORE remote revocation awaits
    consents.resolve([sampleConsent('previous-account')]);
    memories.resolve(paged([sampleNote('previous-account-note')]));

    expect(await pending).toBe(false);
    expect(apply).not.toHaveBeenCalled();
  });

  it('latest signed-in refresh wins when old and new requests settle out of order', async () => {
    const oldConsents = deferred<ConsentDto[]>();
    const oldMemories = deferred<MemoryPageDto>();
    const newConsents = deferred<ConsentDto[]>();
    const newMemories = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const older = view.refresh({
      listConsents: () => oldConsents.promise,
      page: () => oldMemories.promise
    }, apply);
    // A second refresh or new login invalidates the previous response.
    const newer = view.refresh({
      listConsents: () => newConsents.promise,
      page: () => newMemories.promise
    }, apply);
    newConsents.resolve([sampleConsent('new')]);
    newMemories.resolve(paged([sampleNote('new')]));
    expect(await newer).toBe(true);
    oldConsents.resolve([sampleConsent('old')]);
    oldMemories.resolve(paged([sampleNote('old')]));
    expect(await older).toBe(false);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(apply).toHaveBeenCalledWith(
      [sampleConsent('new')], paged([sampleNote('new')]));
  });

  it('swallows stale network failure after erasure or revocation, but reports a current failure', async () => {
    const failed = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const previous = view.refresh({
      listConsents: async () => [],
      page: () => failed.promise
    }, apply);
    view.invalidate(); // new consent, revoke or confirmed erasure
    failed.reject(new Error('Previous account network failed'));
    expect(await previous).toBe(false);
    expect(apply).not.toHaveBeenCalled();

    await expect(view.refresh({
      listConsents: async () => [],
      page: async () => { throw new Error('Current API failure'); }
    }, apply)).rejects.toThrow('Current API failure');
  });

  it('new login can populate the view without accepting an earlier signed-out result', async () => {
    const previous = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const abandoned = view.refresh({
      listConsents: async () => [],
      page: () => previous.promise
    }, apply);
    view.invalidate(); // logout
    expect(await view.refresh({
      listConsents: async () => [sampleConsent('signed-in-again')],
      page: async () => paged([sampleNote('signed-in-again')])
    }, apply)).toBe(true);
    previous.resolve(paged([sampleNote('former-session')]));
    expect(await abandoned).toBe(false);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(apply).toHaveBeenCalledWith(
      [sampleConsent('signed-in-again')], paged([sampleNote('signed-in-again')]));
  });
});


describe('older page lifecycle and canonical ID append', () => {
  it('ignores a late page after logout or a newer refresh', async () => {
    const pending = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const append = vi.fn();
    const older = view.append({ page: async () => pending.promise }, 'cursor-1', append);
    view.invalidate();
    pending.resolve(paged([sampleNote('former-user')], 'cursor-2'));
    expect(await older).toBe(false);
    expect(append).not.toHaveBeenCalled();
  });

  it('ignores old-page errors but reports current-page errors for explicit retry', async () => {
    const pending = deferred<MemoryPageDto>();
    const view = new CloudViewEpoch();
    const old = view.append({ page: async () => pending.promise }, 'cursor', vi.fn());
    view.invalidate();
    pending.reject(new Error('offline previous session'));
    expect(await old).toBe(false);

    await expect(view.append({
      page: async () => { throw new Error('current offline'); }
    }, 'cursor', vi.fn())).rejects.toThrow('current offline');
  });

  it('appends a current page once and deduplicates canonical UUIDs', async () => {
    const view = new CloudViewEpoch();
    const start = [sampleNote('a'), sampleNote('b')];
    let current = start;
    const accepted = await view.append({
      page: async cursor => {
        expect(cursor).toBe('cursor-next');
        return paged([sampleNote('b'), sampleNote('c'), sampleNote('c')], null);
      }
    }, 'cursor-next', page => {
      current = appendUniqueById(current, page.items);
    });
    expect(accepted).toBe(true);
    expect(current.map(item => item.id)).toEqual(['a', 'b', 'c']);
  });

  it('first-page refresh invalidates pending older page even in same login', async () => {
    const view = new CloudViewEpoch();
    const old = deferred<MemoryPageDto>();
    const applyOlder = vi.fn();
    const pending = view.append({ page: async () => old.promise }, 'before', applyOlder);
    const refreshed = await view.refresh({
      listConsents: async () => [sampleConsent('current')],
      page: async () => paged([sampleNote('first')], null)
    }, vi.fn());
    expect(refreshed).toBe(true);
    old.resolve(paged([sampleNote('stale')]));
    expect(await pending).toBe(false);
    expect(applyOlder).not.toHaveBeenCalled();
  });
});
