import { describe, expect, it, vi } from 'vitest';
import { CloudViewEpoch } from '../../src/mobile/src/services/cloudViewEpoch';
import type { ConsentDto, RemoteMemory } from '../../src/mobile/src/services/memoryApi';

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

describe('session-scoped cloud view refresh - no stale personal data', () => {
  it('ignores an authenticated response delivered after logout invalidated the view', async () => {
    const consents = deferred<ConsentDto[]>();
    const memories = deferred<RemoteMemory[]>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const pending = view.refresh({
      listConsents: () => consents.promise,
      recent: () => memories.promise
    }, apply);

    view.invalidate(); // sign-out starts BEFORE remote revocation awaits
    consents.resolve([sampleConsent('previous-account')]);
    memories.resolve([sampleNote('previous-account-note')]);

    expect(await pending).toBe(false);
    expect(apply).not.toHaveBeenCalled();
  });

  it('latest signed-in refresh wins when old and new requests settle out of order', async () => {
    const oldConsents = deferred<ConsentDto[]>();
    const oldMemories = deferred<RemoteMemory[]>();
    const newConsents = deferred<ConsentDto[]>();
    const newMemories = deferred<RemoteMemory[]>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const older = view.refresh({
      listConsents: () => oldConsents.promise,
      recent: () => oldMemories.promise
    }, apply);
    // A second refresh or new login invalidates the previous response.
    const newer = view.refresh({
      listConsents: () => newConsents.promise,
      recent: () => newMemories.promise
    }, apply);
    newConsents.resolve([sampleConsent('new')]);
    newMemories.resolve([sampleNote('new')]);
    expect(await newer).toBe(true);
    oldConsents.resolve([sampleConsent('old')]);
    oldMemories.resolve([sampleNote('old')]);
    expect(await older).toBe(false);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(apply).toHaveBeenCalledWith(
      [sampleConsent('new')], [sampleNote('new')]);
  });

  it('swallows stale network failure after erasure or revocation, but reports a current failure', async () => {
    const failed = deferred<RemoteMemory[]>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const previous = view.refresh({
      listConsents: async () => [],
      recent: () => failed.promise
    }, apply);
    view.invalidate(); // new consent, revoke or confirmed erasure
    failed.reject(new Error('Previous account network failed'));
    expect(await previous).toBe(false);
    expect(apply).not.toHaveBeenCalled();

    await expect(view.refresh({
      listConsents: async () => [],
      recent: async () => { throw new Error('Current API failure'); }
    }, apply)).rejects.toThrow('Current API failure');
  });

  it('new login can populate the view without accepting an earlier signed-out result', async () => {
    const previous = deferred<RemoteMemory[]>();
    const view = new CloudViewEpoch();
    const apply = vi.fn();
    const abandoned = view.refresh({
      listConsents: async () => [],
      recent: () => previous.promise
    }, apply);
    view.invalidate(); // logout
    expect(await view.refresh({
      listConsents: async () => [sampleConsent('signed-in-again')],
      recent: async () => [sampleNote('signed-in-again')]
    }, apply)).toBe(true);
    previous.resolve([sampleNote('former-session')]);
    expect(await abandoned).toBe(false);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(apply).toHaveBeenCalledWith(
      [sampleConsent('signed-in-again')], [sampleNote('signed-in-again')]);
  });
});
