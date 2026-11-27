import type { PushSupport } from './pushClient';

const INVITE_KEY = 'tao.push.invite';

/** What the user said to the offer of notifications: "never" is permanent, "later" lasts for the session. */
export type InviteAnswer = 'never' | 'later' | null;

/** Offer notifications only to a signed-in user who can have them, has not got them and has not said no. */
export function shouldInvite(input: {
  signedIn: boolean; loaded: boolean; support: PushSupport; on: boolean; answer: InviteAnswer; otherPromptOpen: boolean;
}): boolean {
  return input.signedIn && input.loaded && input.support === 'ready' && !input.on && input.answer === null &&
    !input.otherPromptOpen;
}

export function loadInviteAnswer(local: Pick<Storage, 'getItem'> | null, session: Pick<Storage, 'getItem'> | null): InviteAnswer {
  try {
    if (local?.getItem(INVITE_KEY) === 'never') return 'never';
    return session?.getItem(INVITE_KEY) === 'later' ? 'later' : null;
  } catch { return null; }
}

export function saveInviteAnswer(local: Pick<Storage, 'setItem'> | null, session: Pick<Storage, 'setItem'> | null,
  answer: 'never' | 'later'): void {
  try { (answer === 'never' ? local : session)?.setItem(INVITE_KEY, answer); } catch { /* private mode */ }
}
