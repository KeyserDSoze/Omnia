import { describe, expect, it, vi } from 'vitest';
import { ConsentLedgerEpoch, appendUniqueConsents } from '../../src/mobile/src/services/consentLedgerView';
import type { ConsentDto, ConsentPageDto } from '../../src/mobile/src/services/memoryApi';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const grant = (id: string, source: string, active = true): ConsentDto => ({
  id, source, purpose: 'memory.capture', policyVersion: 'privacy-v1',
  grantedAt: '2026-11-23T08:00:00Z', expiresAt: '2026-12-01T08:00:00Z',
  revokedAt: active ? null : '2026-11-23T08:01:00Z', active
});
const page = (items: ConsentDto[], nextCursor: string | null = null): ConsentPageDto =>
  ({ items, nextCursor });

describe('consent-history Web privacy lifecycle #181', () => {
  it('never applies private consent history returned after logout or 401', async () => {
    const pending = deferred<ConsentPageDto>();
    const state = new ConsentLedgerEpoch();
    const apply = vi.fn();
    const read = state.refresh({ consentPage: () => pending.promise }, apply);
    state.invalidate(); // clear session synchronously before provider logout
    pending.resolve(page([grant('old', 'personal-mail')], 'c1.old'));
    expect(await read).toBe(false);
    expect(apply).not.toHaveBeenCalled();
  });

  it('latest refresh wins and a pending prior page cannot replace updated source status', async () => {
    const pending = deferred<ConsentPageDto>();
    const state = new ConsentLedgerEpoch();
    const apply = vi.fn();
    const old = state.refresh({ consentPage: () => pending.promise }, apply);
    const current = state.refresh({
      consentPage: async () => page([grant('new', 'calendar', false)])
    }, apply);
    expect(await current).toBe(true);
    pending.resolve(page([grant('old', 'calendar', true)]));
    expect(await old).toBe(false);
    expect(apply).toHaveBeenCalledExactlyOnceWith(page([grant('new', 'calendar', false)]));
  });

  it('rejects a late older-page result after revocation and appends only current source entries', async () => {
    const pending = deferred<ConsentPageDto>();
    const state = new ConsentLedgerEpoch();
    const apply = vi.fn();
    const old = state.append({ consentPage: () => pending.promise }, 'c1.old', apply);
    state.invalidate(); // revoke/erase/logout/first page refresh
    pending.resolve(page([grant('stale', 'crm')]));
    expect(await old).toBe(false);
    expect(apply).not.toHaveBeenCalled();

    const actual = await state.append({
      consentPage: async cursor => {
        expect(cursor).toBe('c1.current');
        return page([grant('b', 'calendar', false)], null);
      }
    }, 'c1.current', apply);
    expect(actual).toBe(true);
    expect(apply).toHaveBeenCalledExactlyOnceWith(page([grant('b', 'calendar', false)]));
  });

  it('suppresses obsolete failures but preserves real active network errors', async () => {
    const pending = deferred<ConsentPageDto>();
    const state = new ConsentLedgerEpoch();
    const previous = state.refresh({ consentPage: () => pending.promise }, vi.fn());
    state.invalidate();
    pending.reject(new Error('obsolete private consent request'));
    expect(await previous).toBe(false);
    await expect(state.refresh({
      consentPage: async () => { throw new Error('Current 429'); }
    }, vi.fn())).rejects.toThrow('Current 429');
  });

  it('merges duplicated page boundaries by grant UUID without dropping revoked states', () => {
    const result = appendUniqueConsents(
      [grant('a', 'manual'), grant('b', 'calendar')],
      [grant('b', 'calendar'), grant('c', 'crm', false), grant('c', 'crm', false)]
    );
    expect(result.map(g => g.id)).toEqual(['a', 'b', 'c']);
    expect(result[2].active).toBe(false);
    expect(result[2].revokedAt).not.toBeNull();
  });
});
