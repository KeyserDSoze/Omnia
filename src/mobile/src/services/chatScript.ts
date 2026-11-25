import type { SettingsDto } from './setupFlow';

/** Deterministic chat: keywords map to actions; nothing here is "AI". */
export type ChatAction = 'today' | 'briefing' | 'setup' | 'note' | 'memory' | 'settings' | 'unknown';

const RULES: [ChatAction, RegExp][] = [
  ['note', /^\s*(nota|ricorda(mi)?)\b/i],
  ['setup', /\b(configura\w*|setup|collega\w*|account)\b/i],
  ['briefing', /\b(piano|briefing|priorit\w*)\b/i],
  ['today', /\b(oggi|giornata|carte|smista\w*|agenda|cosa devo fare)\b/i],
  ['settings', /\b(impostazioni|preferenze)\b/i],
  ['memory', /\b(memoria|note|privacy|esporta|cancella i miei dati)\b/i]
];

export function intentOf(text: string): ChatAction {
  return RULES.find(([, pattern]) => pattern.test(text))?.[0] ?? 'unknown';
}

/** Text after "nota:" / "ricorda" to prefill the note editor. */
export function noteText(text: string): string {
  return text.replace(/^\s*(nota|ricorda(mi)?)\s*(che\s+)?[:,-]?\s*/i, '').trim();
}

export type DayRecapDto = { day: string; done: number; notDone: number; later: number; dismissed: number };
export type Hint = { id: ChatAction; label: string };

/** Before sign-in Tao only says hello and asks to sign in. */
export function welcomeLines(mode: 'signed-out' | 'demo'): [string, string] {
  return mode === 'demo'
    ? ['Ciao.', 'Questa è la demo di Tao: niente account veri, niente salvato. Entra come faresti con il tuo account.']
    : ['Ciao.', 'Per cominciare, entra con il tuo account.'];
}

/** Shown before the browser's speech services are used: the browser, not Tao, does the work. */
export const SPEECH_NOTICE =
  'Per la voce uso i servizi vocali del tuo browser. La dettatura invia l’audio al fornitore del browser ' +
  '(Google con Chrome, Microsoft con Edge, Apple con Safari) che lo trasforma in testo; se sul dispositivo ' +
  'non c’è una voce italiana, anche il testo che leggo va alla sua voce online. ' +
  'Tao non registra e non salva l’audio: riceve solo il testo, come se l’avessi scritto. ' +
  'Attivo i servizi vocali per 30 giorni? Puoi revocarli quando vuoi dalle impostazioni.';

/** Typewriter duration for a line: quick, proportional, capped. */
export function typingMs(text: string): number {
  return Math.min(1400, Math.max(220, text.length * 16));
}

const plain = (text: string) => text.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase()
  .replace(/[^a-z0-9: ]+/g, ' ').replace(/\s+/g, ' ').trim();
/** The part of a label before ":" or "(" ("Outlook: posta…" -> "outlook"). */
const keyOf = (label: string) => plain(label.split(/[:(]/)[0]);

/** A spoken or typed answer for a single-choice step; null when unknown or ambiguous. */
export function matchChoice(choices: readonly { id: string; label: string }[], text: string): string | null {
  const said = plain(text).replace(/:/g, '');
  if (!said) return null;
  const exact = choices.filter(c => plain(c.id) === said || plain(c.label).replace(/:/g, '') === said);
  if (exact.length === 1) return exact[0].id;
  const close = choices.filter(c => {
    const key = keyOf(c.label);
    return key === said || key.startsWith(said + ' ') || said.startsWith(key + ' ');
  });
  return close.length === 1 ? close[0].id : null;
}

/** Choices named in a spoken or typed answer for a multiple-choice step. */
export function matchChoices(choices: readonly { id: string; label: string }[], text: string): string[] {
  const said = ' ' + plain(text) + ' ';
  return choices.filter(c => said.includes(' ' + keyOf(c.label) + ' ')).map(c => c.id);
}

export function greeting(name: string | null, settings: SettingsDto | null, now: Date): string {
  const hour = now.getHours();
  const hello = hour < 12 ? 'Buongiorno' : hour < 18 ? 'Buon pomeriggio' : 'Buonasera';
  const who = settings?.displayName ?? name;
  return who ? `${hello}, ${who}!` : `${hello}!`;
}

/** Starting question and hints from the previous days' decisions (counts only, no content). */
export function openingLines(settings: SettingsDto | null, recap: readonly DayRecapDto[]): string[] {
  if (!settings?.onboardingCompletedAt)
    return ['Sono Tao, il tuo assistente di lavoro. Prima di iniziare configuriamo insieme i tuoi account: bastano due minuti, e decidi tu cosa posso leggere e fare.'];
  const lines: string[] = [];
  const yesterday = recap.length >= 2 ? recap[recap.length - 2] : null;
  const earlier = recap.slice(0, -1);
  const postponed = earlier.reduce((sum, d) => sum + d.later, 0);
  const notDone = earlier.reduce((sum, d) => sum + d.notDone, 0);
  if (yesterday && yesterday.done > 0) lines.push(`Ieri hai chiuso ${yesterday.done} ${yesterday.done === 1 ? 'carta' : 'carte'}.`);
  if (postponed > 0) lines.push(`Nei giorni scorsi hai rimandato ${postponed} ${postponed === 1 ? 'carta' : 'carte'}: torneranno in coda.`);
  if (notDone > 0) lines.push(`${notDone} ${notDone === 1 ? 'carta è rimasta "non fatta"' : 'carte sono rimaste "non fatte"'}: vale la pena rivederle.`);
  const unconnected = settings.accounts.filter(a => !a.connectedAccountId).length;
  if (unconnected > 0) lines.push(`${unconnected} ${unconnected === 1 ? 'account non è ancora collegato' : 'account non sono ancora collegati'}.`);
  lines.push('Da dove partiamo oggi?');
  return lines;
}

export function hints(settings: SettingsDto | null, assistant: boolean): Hint[] {
  if (!settings?.onboardingCompletedAt)
    return [{ id: 'setup', label: '✨ Configura Tao' }, { id: 'memory', label: 'Le mie note' }];
  const list: Hint[] = [{ id: 'today', label: 'Smista la giornata' }];
  if (assistant) list.push({ id: 'briefing', label: 'Piano del giorno (AI)' });
  if (settings.accounts.some(a => !a.connectedAccountId)) list.push({ id: 'setup', label: 'Collega gli account' });
  list.push({ id: 'note', label: 'Salva una nota' }, { id: 'settings', label: 'Impostazioni' });
  return list;
}
