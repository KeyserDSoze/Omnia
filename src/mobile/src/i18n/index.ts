import { useCallback, useSyncExternalStore } from 'react';
import { detectLocale, localeInfo, type Locale } from './locales';
import { it } from './messages/it';
import { en } from './messages/en';
import { zh } from './messages/zh';
import { hi } from './messages/hi';
import { es } from './messages/es';
import { fr } from './messages/fr';
import { ar } from './messages/ar';
import { bn } from './messages/bn';
import { pt } from './messages/pt';
import { ru } from './messages/ru';
import { ur } from './messages/ur';
import { id } from './messages/id';
import { de } from './messages/de';
import { ja } from './messages/ja';
import { ko } from './messages/ko';
import { tr } from './messages/tr';
import { vi } from './messages/vi';
import { fa } from './messages/fa';
import { pl } from './messages/pl';
import { nl } from './messages/nl';
import { th } from './messages/th';

export * from './locales';

/** Forms chosen with Intl.PluralRules; "other" is always required. */
export type Plural = { zero?: string; one?: string; two?: string; few?: string; many?: string; other: string };
type Source = typeof it;
export type MessageKey = keyof Source;
/** Every language has exactly the Italian keys, with a plural wherever Italian has one. */
export type Messages = { readonly [K in MessageKey]: Source[K] extends string ? string : Plural };
export type Params = Record<string, string | number>;

export const catalogs: Record<Locale, Messages> = {
  it, en, zh, hi, es, fr, ar, bn, pt, ru, ur, id, de, ja, ko, tr, vi, fa, pl, nl, th
};

/** The static export is rendered in English; the browser language applies after hydration. */
const RENDER_LOCALE: Locale = 'en';
const STORAGE_KEY = 'tao.locale';

function initialLocale(): Locale {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return RENDER_LOCALE;
  let stored: string | null = null;
  try { stored = window.localStorage?.getItem(STORAGE_KEY) ?? null; } catch { /* storage disabled */ }
  const preferences = navigator.languages?.length ? navigator.languages : [navigator.language ?? ''];
  return detectLocale(stored, preferences);
}

let current: Locale = initialLocale();
const listeners = new Set<() => void>();
const plurals = new Map<Locale, Intl.PluralRules>();
const numbers = new Map<Locale, Intl.NumberFormat>();

export const getLocale = (): Locale => current;
export const intlTag = (locale: Locale = current): string => localeInfo(locale).tag;

/** Changes the language everywhere; the choice is remembered only in this browser. */
export function setLocale(next: Locale, remember = true) {
  current = next;
  if (remember) {
    try { window.localStorage?.setItem(STORAGE_KEY, next); } catch { /* storage disabled */ }
  }
  listeners.forEach(listener => listener());
}

export function translate(locale: Locale, key: MessageKey, params?: Params): string {
  const entry: string | Plural = catalogs[locale][key] ?? catalogs.en[key];
  let text: string;
  if (typeof entry === 'string') text = entry;
  else {
    let rules = plurals.get(locale);
    if (!rules) plurals.set(locale, rules = new Intl.PluralRules(intlTag(locale)));
    text = entry[rules.select(Number(params?.count ?? 0)) as keyof Plural] ?? entry.other;
  }
  if (!params) return text;
  return text.replace(/\{(\w+)\}/g, (match, name: string) => {
    const value = params[name];
    if (value === undefined) return match;
    if (typeof value === 'string') return value;
    let format = numbers.get(locale);
    if (!format) numbers.set(locale, format = new Intl.NumberFormat(intlTag(locale)));
    return format.format(value);
  });
}

/** For services and event handlers; render code uses useT(), which is hydration-safe. */
export const t = (key: MessageKey, params?: Params): string => translate(current, key, params);

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
};

export function useLocale(): Locale {
  return useSyncExternalStore(subscribe, getLocale, () => RENDER_LOCALE);
}

export function useT() {
  const locale = useLocale();
  return useCallback((key: MessageKey, params?: Params) => translate(locale, key, params), [locale]);
}
