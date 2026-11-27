import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { describe, expect, it } from 'vitest';
import { checkReminder, reminderLine } from '../../src/mobile/src/services/reminders';
import { keyBytes } from '../../src/mobile/src/services/pushKey';
import type { ReminderDto } from '../../src/mobile/src/services/memoryApi';

describe('the reminder form', () => {
  const ok = { text: 'Chiama Marco', time: '09:30', repeat: 'daily' as const, date: '' };

  it('accepts what the server accepts', () => {
    expect(checkReminder(ok)).toBe(true);
    expect(checkReminder({ ...ok, repeat: 'once', date: '2026-12-03' })).toBe(true);
    expect(checkReminder({ ...ok, text: 'x'.repeat(200) })).toBe(true);
  });

  it.each([
    ['empty text', { text: '   ' }], ['long text', { text: 'x'.repeat(201) }], ['control character', { text: 'a\nb' }],
    ['no minutes', { time: '9' }], ['hour 24', { time: '24:00' }], ['minute 60', { time: '09:60' }],
    ['one-off without date', { repeat: 'once' as const, date: '' }],
    ['impossible date', { repeat: 'once' as const, date: '2026-02-30' }],
    ['wrong date shape', { repeat: 'once' as const, date: '03/12/2026' }]
  ])('refuses %s', (_name, change) => expect(checkReminder({ ...ok, ...change })).toBe(false));

  it('ignores the date of a repeating reminder', () => expect(checkReminder({ ...ok, date: 'garbage' })).toBe(true));
});

describe('the reminder list', () => {
  const reminder = (over: Partial<ReminderDto>): ReminderDto => ({
    id: 'id', text: 'Dentista', repeat: 'once', time: '15:00', date: '2026-12-03', timeZone: 'Europe/Rome', enabled: true,
    nextRunAt: null, lastSentAt: null, ...over
  });

  it('shows the date only for a one-off', () => {
    expect(reminderLine(reminder({}), 'Una volta')).toBe('Dentista \u00b7 Una volta \u00b7 2026-12-03 15:00');
    expect(reminderLine(reminder({ repeat: 'daily', date: null }), 'Ogni giorno')).toBe('Dentista \u00b7 Ogni giorno \u00b7 15:00');
  });
});

describe('the server key for the browser', () => {
  it('turns base64url into the bytes the browser expects', () => {
    const key = 'BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8';
    const bytes = keyBytes(key);
    expect(bytes.length).toBe(65);
    expect(bytes[0]).toBe(4);
  });
});

describe('what the service worker shows for a push', () => {
  const source = readFileSync(new URL('../../src/mobile/public/sw.js', import.meta.url), 'utf8');
  const sandbox: Record<string, unknown> = {
    self: { registration: { scope: 'https://taoai.me/' }, location: { origin: 'https://taoai.me' }, addEventListener: undefined },
    module: { exports: {} }, URL, Set, Promise
  };
  vm.runInNewContext(source, sandbox);
  const { notificationOf } = (sandbox.module as { exports: { notificationOf: (d: unknown, scope: string) => { title: string; options: Record<string, unknown> } } }).exports;

  it('shows the text and always opens the app itself', () => {
    const shown = notificationOf({ title: 'Tao', body: 'Chiama Marco', tag: 'abc', url: 'https://evil.example' }, 'https://taoai.me/');
    expect(shown.title).toBe('Tao');
    expect(shown.options).toMatchObject({ body: 'Chiama Marco', tag: 'abc', icon: 'https://taoai.me/icon-192.png', data: { url: 'https://taoai.me/' } });
  });

  it('bounds the text and survives a payload that is not what was expected', () => {
    expect(notificationOf({ body: 'x'.repeat(500) }, 'https://taoai.me/').options.body).toHaveLength(200);
    for (const odd of [null, undefined, 'text', 42, { title: 7, body: {} }])
      expect(notificationOf(odd, 'https://taoai.me/')).toMatchObject({ title: 'Tao', options: { body: '' } });
  });
});
