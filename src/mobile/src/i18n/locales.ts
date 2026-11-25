/**
 * The languages Tao speaks: Italian (source) plus the 20 most important world languages,
 * chosen by speakers (Ethnologue, without pidgins and regional varieties) and digital weight.
 */
export const LOCALES = [
  { code: 'it', name: 'Italiano', english: 'Italian', tag: 'it-IT' },
  { code: 'en', name: 'English', english: 'English', tag: 'en-US' },
  { code: 'zh', name: '中文（简体）', english: 'Chinese (Simplified)', tag: 'zh-CN', unspaced: true },
  { code: 'hi', name: 'हिन्दी', english: 'Hindi', tag: 'hi-IN' },
  { code: 'es', name: 'Español', english: 'Spanish', tag: 'es-ES' },
  { code: 'fr', name: 'Français', english: 'French', tag: 'fr-FR' },
  { code: 'ar', name: 'العربية', english: 'Arabic', tag: 'ar-EG', rtl: true },
  { code: 'bn', name: 'বাংলা', english: 'Bengali', tag: 'bn-BD' },
  { code: 'pt', name: 'Português (Brasil)', english: 'Portuguese (Brazil)', tag: 'pt-BR' },
  { code: 'ru', name: 'Русский', english: 'Russian', tag: 'ru-RU' },
  { code: 'ur', name: 'اردو', english: 'Urdu', tag: 'ur-PK', rtl: true },
  { code: 'id', name: 'Bahasa Indonesia', english: 'Indonesian', tag: 'id-ID' },
  { code: 'de', name: 'Deutsch', english: 'German', tag: 'de-DE' },
  { code: 'ja', name: '日本語', english: 'Japanese', tag: 'ja-JP', unspaced: true },
  { code: 'ko', name: '한국어', english: 'Korean', tag: 'ko-KR' },
  { code: 'tr', name: 'Türkçe', english: 'Turkish', tag: 'tr-TR' },
  { code: 'vi', name: 'Tiếng Việt', english: 'Vietnamese', tag: 'vi-VN' },
  { code: 'fa', name: 'فارسی', english: 'Persian', tag: 'fa-IR', rtl: true },
  { code: 'pl', name: 'Polski', english: 'Polish', tag: 'pl-PL' },
  { code: 'nl', name: 'Nederlands', english: 'Dutch', tag: 'nl-NL' },
  { code: 'th', name: 'ไทย', english: 'Thai', tag: 'th-TH', unspaced: true }
] as const;

export type Locale = (typeof LOCALES)[number]['code'];
export type LocaleInfo = { code: Locale; name: string; english: string; tag: string; rtl?: boolean; unspaced?: boolean };

const byCode = new Map<string, LocaleInfo>(LOCALES.map(l => [l.code, l]));

export const isLocale = (value: unknown): value is Locale => typeof value === 'string' && byCode.has(value);
export const localeInfo = (code: Locale): LocaleInfo => byCode.get(code)!;

/** First supported language among the user's preferences ("pt-BR" -> pt, "zh-TW" -> zh). */
export function matchLocale(preferences: readonly string[]): Locale | null {
  for (const preference of preferences) {
    const base = preference.toLowerCase().split(/[-_]/)[0];
    if (isLocale(base)) return base;
  }
  return null;
}

/** An explicit choice wins, then the browser languages, then English. */
export function detectLocale(stored: string | null, preferences: readonly string[]): Locale {
  return isLocale(stored) ? stored : matchLocale(preferences) ?? 'en';
}
