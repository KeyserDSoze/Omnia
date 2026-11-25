import type { SettingsDto } from './setupFlow';
import { getLocale, localeInfo, t, translate, type Locale, type MessageKey } from '../i18n';

/** Deterministic chat: keywords map to actions; nothing here is "AI". */
export type ChatAction = 'today' | 'briefing' | 'setup' | 'note' | 'memory' | 'settings' | 'unknown';

const INTENTS: [Exclude<ChatAction, 'note' | 'unknown'>, MessageKey][] = [
  ['setup', 'intent.setup'], ['briefing', 'intent.briefing'], ['today', 'intent.today'],
  ['settings', 'intent.settings'], ['memory', 'intent.memory']
];

/** Lowercase, no Latin accents, punctuation as spaces: comparable in every script. */
export function normalizeText(text: string): string {
  return text.toLowerCase().normalize('NFD').replace(/(\p{Script=Latin})\p{M}+/gu, '$1').normalize('NFC')
    .replace(/[^\p{L}\p{M}\p{N}:]+/gu, ' ').trim();
}

const isWordChar = (c: string | undefined) => c !== undefined && /[\p{L}\p{M}\p{N}]/u.test(c);
/** The user's language first, then English, which many people type anyway. */
const languages = (locale: Locale): Locale[] => locale === 'en' ? ['en'] : [locale, 'en'];

function keywords(locale: Locale, key: MessageKey) {
  return translate(locale, key).split('|').map(raw => raw.trim()).filter(Boolean)
    .map(raw => ({ word: normalizeText(raw.replace(/\*$/, '')), prefix: raw.endsWith('*') }))
    .filter(k => k.word.length > 0)
    .sort((a, b) => b.word.length - a.word.length);
}

/** Whole-word match (a trailing * allows any ending); scripts without spaces match anywhere. */
function mentions(text: string, word: string, prefix: boolean, unspaced: boolean): boolean {
  for (let at = text.indexOf(word); at !== -1; at = text.indexOf(word, at + 1)) {
    const end = at + word.length;
    if (unspaced || ((at === 0 || !isWordChar(text[at - 1])) && (prefix || !isWordChar(text[end])))) return true;
  }
  return false;
}

export function intentOf(text: string, locale: Locale = getLocale()): ChatAction {
  const said = normalizeText(text);
  for (const language of languages(locale)) {
    const unspaced = localeInfo(language).unspaced === true;
    // A note is recognized only at the start: «nota: …», «remember …».
    if (keywords(language, 'intent.note').some(k => said.startsWith(k.word) &&
      (unspaced || k.prefix || !isWordChar(said[k.word.length])))) return 'note';
    for (const [action, key] of INTENTS)
      if (keywords(language, key).some(k => mentions(said, k.word, k.prefix, unspaced))) return action;
  }
  return 'unknown';
}

/** Text after «nota:» / «remember that …», to prefill the note editor (original casing kept). */
export function noteText(text: string, locale: Locale = getLocale()): string {
  const trimmed = text.trim();
  const strip = (value: string, words: string[], unspaced: boolean) => {
    const found = words.find(raw => {
      const word = raw.replace(/\*$/, '');
      return value.slice(0, word.length).toLowerCase() === word.toLowerCase() &&
        (unspaced || raw.endsWith('*') || !isWordChar(value[word.length]));
    });
    return found === undefined ? null : value.slice(found.replace(/\*$/, '').length).trimStart();
  };
  for (const language of languages(locale)) {
    const unspaced = localeInfo(language).unspaced === true;
    const list = (key: MessageKey) => translate(language, key).split('|').map(w => w.trim()).filter(Boolean)
      .sort((a, b) => b.length - a.length);
    const rest = strip(trimmed, list('intent.note'), unspaced);
    if (rest === null) continue;
    const linked = strip(rest, list('intent.noteLink'), unspaced) ?? rest;
    return linked.replace(/^[:,\-–—：、，]+/u, '').trim();
  }
  return trimmed;
}

export type DayRecapDto = { day: string; done: number; notDone: number; later: number; dismissed: number };
export type Hint = { id: ChatAction; label: string };

/** Before sign-in Tao only says hello and asks to sign in. */
export function welcomeLines(mode: 'signed-out' | 'demo'): [string, string] {
  return [t('welcome.hello'), t(mode === 'demo' ? 'welcome.demo' : 'welcome.signIn')];
}

/** Typewriter duration for a line: quick, proportional, capped. */
export function typingMs(text: string): number {
  return Math.min(1400, Math.max(220, text.length * 16));
}

/** The part of a label before ":" or "(" ("Outlook: posta…" -> "outlook"). */
const keyOf = (label: string) => normalizeText(label.split(/[:(：（]/)[0]);

/** A spoken or typed answer for a single-choice step; null when unknown or ambiguous. */
export function matchChoice(choices: readonly { id: string; label: string }[], text: string): string | null {
  const said = normalizeText(text).replace(/:/g, '');
  if (!said) return null;
  const exact = choices.filter(c => normalizeText(c.id) === said || normalizeText(c.label).replace(/:/g, '') === said);
  if (exact.length === 1) return exact[0].id;
  const close = choices.filter(c => {
    const key = keyOf(c.label);
    return key === said || key.startsWith(said + ' ') || said.startsWith(key + ' ');
  });
  return close.length === 1 ? close[0].id : null;
}

/** Choices named in a spoken or typed answer for a multiple-choice step. */
export function matchChoices(choices: readonly { id: string; label: string }[], text: string): string[] {
  const said = ' ' + normalizeText(text) + ' ';
  return choices.filter(c => said.includes(' ' + keyOf(c.label) + ' ')).map(c => c.id);
}

export function greeting(name: string | null, settings: SettingsDto | null, now: Date): string {
  const hour = now.getHours();
  const part = hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
  const who = settings?.displayName ?? name;
  return who ? t(`greet.${part}Name`, { name: who }) : t(`greet.${part}`);
}

/** Starting question and hints from the previous days' decisions (counts only, no content). */
export function openingLines(settings: SettingsDto | null, recap: readonly DayRecapDto[]): string[] {
  if (!settings?.onboardingCompletedAt) return [t('opening.setupFirst')];
  const lines: string[] = [];
  const yesterday = recap.length >= 2 ? recap[recap.length - 2] : null;
  const earlier = recap.slice(0, -1);
  const postponed = earlier.reduce((sum, d) => sum + d.later, 0);
  const notDone = earlier.reduce((sum, d) => sum + d.notDone, 0);
  if (yesterday && yesterday.done > 0) lines.push(t('opening.closedYesterday', { count: yesterday.done }));
  if (postponed > 0) lines.push(t('opening.postponed', { count: postponed }));
  if (notDone > 0) lines.push(t('opening.notDone', { count: notDone }));
  const unconnected = settings.accounts.filter(a => !a.connectedAccountId).length;
  if (unconnected > 0) lines.push(t('opening.unconnected', { count: unconnected }));
  lines.push(t('opening.where'));
  return lines;
}

export function hints(settings: SettingsDto | null, assistant: boolean): Hint[] {
  if (!settings?.onboardingCompletedAt)
    return [{ id: 'setup', label: t('hint.setup') }, { id: 'memory', label: t('hint.memory') }];
  const list: Hint[] = [{ id: 'today', label: t('hint.today') }];
  if (assistant) list.push({ id: 'briefing', label: t('hint.briefing') });
  if (settings.accounts.some(a => !a.connectedAccountId)) list.push({ id: 'setup', label: t('hint.connect') });
  list.push({ id: 'note', label: t('hint.note') }, { id: 'settings', label: t('hint.settings') });
  return list;
}
