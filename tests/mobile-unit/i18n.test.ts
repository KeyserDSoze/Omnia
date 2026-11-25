import { describe, expect, it } from 'vitest';
import {
  LOCALES, catalogs, detectLocale, isLocale, matchLocale, setLocale, translate, type Locale, type MessageKey
} from '../../src/mobile/src/i18n';
import { intentOf, noteText, normalizeText } from '../../src/mobile/src/services/chatScript';
import { classifyMemo } from '../../src/mobile/src/domain/tags';

const keys = Object.keys(catalogs.it) as MessageKey[];
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();
const forms = (entry: string | Record<string, string>) => typeof entry === 'string' ? [entry] : Object.values(entry);

describe('language catalogs', () => {
  it('speaks 21 languages: Italian plus the 20 most spoken', () => {
    expect(LOCALES).toHaveLength(21);
    expect(new Set(LOCALES.map(l => l.code)).size).toBe(21);
    expect(LOCALES.filter(l => 'rtl' in l).map(l => l.code)).toEqual(['ar', 'ur', 'fa']);
  });

  it.each(LOCALES.map(l => l.code))('%s has every key, with the same placeholders as Italian', (code: Locale) => {
    const catalog = catalogs[code] as Record<string, string | Record<string, string>>;
    expect(Object.keys(catalog).sort()).toEqual([...keys].sort());
    for (const key of keys) {
      const source = catalogs.it[key] as string | Record<string, string>;
      const entry = catalog[key];
      expect(typeof entry, key).toBe(typeof source);
      if (typeof entry === 'object') expect(typeof entry.other, `${key}.other`).toBe('string');
      // Some plural forms say "one card" without the number, so compare against the union.
      const used = new Set(forms(entry).flatMap(placeholders));
      const wanted = new Set(forms(source).flatMap(placeholders));
      for (const name of used) expect(wanted.has(name), `${code} ${key} uses {${name}}`).toBe(true);
      if (typeof source === 'string') expect([...used].sort(), `${code} ${key}`).toEqual([...wanted].sort());
      expect(forms(entry).every(text => text.trim() !== ''), `${code} ${key} is empty`).toBe(true);
    }
  });

  it.each(LOCALES.map(l => l.code))('%s keeps the same intent keyword lists shape and a note word', (code: Locale) => {
    for (const key of ['intent.note', 'intent.setup', 'intent.briefing', 'intent.today', 'intent.settings', 'intent.memory'] as const)
      expect(translate(code, key).split('|').filter(w => w.trim()).length, `${code} ${key}`).toBeGreaterThan(0);
  });
});

describe('choosing a language', () => {
  it('matches the browser languages by their primary subtag', () => {
    expect(matchLocale(['pt-BR'])).toBe('pt');
    expect(matchLocale(['zh-TW', 'en'])).toBe('zh');
    expect(matchLocale(['xx', 'fa-IR'])).toBe('fa');
    expect(matchLocale(['xx'])).toBeNull();
  });

  it('prefers an explicit choice, then the browser, then English', () => {
    expect(detectLocale('de', ['fr'])).toBe('de');
    expect(detectLocale('klingon', ['fr-CA'])).toBe('fr');
    expect(detectLocale(null, ['xx'])).toBe('en');
    expect(isLocale('ja')).toBe(true);
    expect(isLocale('JA')).toBe(false);
  });

  it('picks the plural form of each language and formats numbers', () => {
    expect(translate('en', 'opening.closedYesterday', { count: 1 })).toBe('Yesterday you closed 1 card.');
    expect(translate('en', 'opening.closedYesterday', { count: 5 })).toBe('Yesterday you closed 5 cards.');
    expect(translate('ru', 'opening.closedYesterday', { count: 2 })).toContain('2 карточки');
    expect(translate('ru', 'opening.closedYesterday', { count: 5 })).toContain('5 карточек');
    expect(translate('pl', 'opening.closedYesterday', { count: 3 })).toContain('3 karty');
    expect(translate('ar', 'opening.closedYesterday', { count: 2 })).toContain('بطاقتين');
    expect(translate('ja', 'opening.closedYesterday', { count: 7 })).toContain('7');
    expect(translate('en', 'summary.name', { value: 'x' })).toBe('• Name: x');
  });

  it('falls back to English for a missing key and leaves unknown placeholders alone', () => {
    expect(translate('it', 'ledger.count', {})).toBe('Visualizzati {count} consensi.');
  });
});

describe('chat intents in other languages', () => {
  const said: [Locale, string, ReturnType<typeof intentOf>][] = [
    ['en', 'what should I do today?', 'today'],
    ['en', 'remember that the demo is tomorrow', 'note'],
    ['es', '¿Qué tengo que hacer hoy?', 'today'],
    ['es', 'abre los ajustes', 'settings'],
    ['fr', 'Qu’ai-je à faire aujourd’hui ?', 'today'],
    ['de', 'Was steht heute an?', 'today'],
    ['de', 'Notiz: Anruf bei Müller', 'note'],
    ['pt', 'conectar minha conta', 'setup'],
    ['ru', 'что мне делать сегодня', 'today'],
    ['ru', 'заметка: позвонить клиенту', 'note'],
    ['zh', '我今天该做什么', 'today'],
    ['zh', '笔记：明天开会', 'note'],
    ['ja', '今日の予定を整理して', 'today'],
    ['ko', '오늘 뭘 해야 해?', 'today'],
    ['ko', '메모해줘 내일 회의', 'note'],
    ['ar', 'ماذا أفعل اليوم', 'today'],
    ['hi', 'आज मुझे क्या करना है', 'today'],
    ['tr', 'bugün ne yapmalıyım', 'today'],
    ['th', 'วันนี้ต้องทำอะไร', 'today'],
    ['vi', 'hôm nay tôi phải làm gì', 'today']
  ];

  it.each(said)('%s: “%s” means %s', (locale, text, intent) => {
    expect(intentOf(text, locale)).toBe(intent);
  });

  it('still understands English in any language, and answers unknown when nothing matches', () => {
    expect(intentOf('what should I do today?', 'fr')).toBe('today');
    expect(intentOf('xyzzy plugh', 'de')).toBe('unknown');
  });

  it('strips the note word and its link, keeping the original casing', () => {
    expect(noteText('Remember that Bob called', 'en')).toBe('Bob called');
    expect(noteText('Notiz: Anruf bei Müller', 'de')).toBe('Anruf bei Müller');
    expect(noteText('笔记：明天开会', 'zh')).toBe('明天开会');
    expect(noteText('just text', 'en')).toBe('just text');
  });

  it('normalizes Latin accents but keeps other scripts intact', () => {
    expect(normalizeText('Qué Tengo… hoy?')).toBe('que tengo hoy');
    expect(normalizeText('привет, мир!')).toBe('привет мир');
    expect(normalizeText('नमस्ते')).toBe('नमस्ते');
  });

  it('tags a note from the words of its language, Italian and English', () => {
    expect(classifyMemo('Reunión con el cliente', 'es')).toEqual(['lavoro']);
    expect(classifyMemo('Meeting mit dem Kunden', 'de')).toEqual(['lavoro']);
    expect(classifyMemo('Cena con la famiglia', 'it')).toEqual(['relazioni']);
    expect(classifyMemo('xyz', 'ja')).toEqual(['personale']);
  });
});

describe('the language is remembered only when asked', () => {
  it('changes the language without touching storage when not remembered', () => {
    setLocale('de', false);
    expect(translate('de', 'nav.signIn')).toBe('Anmelden');
    setLocale('it', false);
  });
});
