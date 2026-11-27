import { describe, expect, it } from 'vitest';
import { loadInviteAnswer, saveInviteAnswer, shouldInvite } from '../../src/mobile/src/services/pushInvite';

const eligible = { signedIn: true, loaded: true, support: 'ready' as const, on: false, answer: null, otherPromptOpen: false };

describe('the offer of notifications', () => {
  it('is made to a signed-in user who can have notifications and has not got them', () => {
    expect(shouldInvite(eligible)).toBe(true);
  });

  it.each([
    ['signed out', { signedIn: false }], ['still loading', { loaded: false }], ['browser without push', { support: 'unsupported' as const }],
    ['permission blocked', { support: 'denied' as const }], ['already on', { on: true }], ['said later', { answer: 'later' as const }],
    ['said never', { answer: 'never' as const }], ['another prompt is open', { otherPromptOpen: true }]
  ])('is not made when %s', (_name, change) => expect(shouldInvite({ ...eligible, ...change })).toBe(false));
});

describe('the answer to the offer', () => {
  const store = () => {
    const data = new Map<string, string>();
    return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
  };

  it('"never" is kept for good and "later" only for the session', () => {
    const local = store(), session = store();
    expect(loadInviteAnswer(local, session)).toBeNull();
    saveInviteAnswer(local, session, 'later');
    expect(loadInviteAnswer(local, session)).toBe('later');
    expect(loadInviteAnswer(local, store())).toBeNull(); // a new session asks again
    saveInviteAnswer(local, session, 'never');
    expect(loadInviteAnswer(local, store())).toBe('never');
  });

  it('survives a browser that refuses storage', () => {
    const broken = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); } };
    expect(loadInviteAnswer(broken, broken)).toBeNull();
    expect(() => saveInviteAnswer(broken, broken, 'never')).not.toThrow();
    expect(loadInviteAnswer(null, null)).toBeNull();
  });
});
