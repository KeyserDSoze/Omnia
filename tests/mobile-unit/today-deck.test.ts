import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  canApplyToSource, deckShortcut, decisionCounts, deckWindow, pendingCards, safeWebLink, tomorrowSnoozeMinutes,
  whenLabel, withDecision, withoutDecision
} from '../../src/mobile/src/services/todayDeck';
import { createDemoToday } from '../../src/mobile/src/services/demoToday';
import { MemoryApi, type TodayItemDto, type WorkItemDto } from '../../src/mobile/src/services/memoryApi';

const now = new Date('2026-11-25T08:00:00Z');
const item = (over: Partial<WorkItemDto>): WorkItemDto => ({
  key: 'microsoft:' + 'a'.repeat(32) + ':mail:' + (over.externalId ?? 'x'),
  provider: 'microsoft', accountId: 'a'.repeat(32), kind: 'mail', externalId: 'x', title: 'T',
  detail: null, counterpart: null, startsAt: null, endsAt: null, dueAt: null,
  important: false, unread: false, webLink: null, ...over
});
const entry = (over: Partial<WorkItemDto>, triage: TodayItemDto['triage'] = null): TodayItemDto => {
  const value = item(over);
  return { key: value.key, item: value, priority: 50, triage };
};
afterEach(() => vi.unstubAllGlobals());

describe('daily deck', () => {
  it('uses the local calendar day and keeps two previous days for cards queued for tomorrow (DST-safe)', () => {
    const window = deckWindow(new Date(2026, 2, 29, 10, 0, 0)); // Europe DST switch day
    expect(window.dayStart.getHours()).toBe(0);
    expect(window.to.getDate()).toBe(30);
    expect(window.to.getHours()).toBe(0);
    expect(window.dayStart.getTime() - window.from.getTime()).toBe(48 * 3600_000);
    expect(window.to.getTime() - window.from.getTime()).toBeLessThanOrEqual(96 * 3600_000);
  });

  it('hides stale undecided mail but brings back what was queued for tomorrow', () => {
    const queued: TodayItemDto['triage'] = { decision: 'later', decidedAt: '', appliedToSource: false,
      snoozedUntil: '2026-11-25T07:00:00Z' };
    const items = [
      entry({ externalId: 'old', startsAt: '2026-11-23T10:00:00Z' }),
      entry({ externalId: 'recent', startsAt: '2026-11-24T20:00:00Z' }),
      entry({ externalId: 'queued', startsAt: '2026-11-24T10:00:00Z' }, queued),
      entry({ externalId: 'task', kind: 'task' })
    ];
    expect(pendingCards(items, now, new Date('2026-11-25T00:00:00Z')).map(x => x.item.externalId))
      .toEqual(['recent', 'queued', 'task']);
  });

  it('computes the minutes until tomorrow morning in local time', () => {
    expect(tomorrowSnoozeMinutes(new Date(2026, 10, 25, 20, 30))).toBe(690);
    expect(tomorrowSnoozeMinutes(new Date(2026, 10, 25, 2, 0))).toBe(1800);
    expect(tomorrowSnoozeMinutes(new Date(2026, 10, 25, 20, 30), 9)).toBe(750);
    expect(tomorrowSnoozeMinutes(new Date(2026, 10, 25, 23, 59))).toBeGreaterThanOrEqual(15);
  });

  it('puts a card back by dropping its decision locally', () => {
    const items = [entry({ externalId: 'a' }, { decision: 'dismissed', decidedAt: '', snoozedUntil: null, appliedToSource: false }),
      entry({ externalId: 'b' }, { decision: 'done', decidedAt: '', snoozedUntil: null, appliedToSource: false })];
    const next = withoutDecision(items, items[0].key);
    expect(next.map(x => x.triage?.decision ?? null)).toEqual([null, 'done']);
    expect(items[0].triage?.decision).toBe('dismissed');
  });

  it('maps unmodified keys to deck actions', () => {
    expect(deckShortcut({ key: 'ArrowRight' })).toBe('done');
    expect(deckShortcut({ key: 'ArrowLeft' })).toBe('not_done');
    expect(deckShortcut({ key: 'ArrowDown' })).toBe('later');
    expect(deckShortcut({ key: 'ArrowUp' })).toBe('tomorrow');
    expect(deckShortcut({ key: 'Delete' })).toBe('dismissed');
    expect(deckShortcut({ key: 'Backspace' })).toBe('dismissed');
    expect(deckShortcut({ key: 'z' })).toBe('undo');
    expect(deckShortcut({ key: 'Z' })).toBe('undo');
    expect(deckShortcut({ key: 'Enter' })).toBeNull();
    expect(deckShortcut({ key: 'z', ctrlKey: true })).toBeNull();
    expect(deckShortcut({ key: 'ArrowRight', metaKey: true })).toBeNull();
    expect(deckShortcut({ key: 'ArrowLeft', altKey: true })).toBeNull();
  });

  it('the demo deck undoes a decision', async () => {
    const demo = createDemoToday(() => now);
    const first = (await demo.today(now, now)).items[0];
    await demo.decide(first.key, 'done');
    expect((await demo.today(now, now)).items[0].triage?.decision).toBe('done');
    await demo.undoDecision(first.key);
    expect((await demo.today(now, now)).items[0].triage).toBeNull();
  });

  it('shows undecided cards and snoozed cards only after the snooze expires', () => {
    const items = [
      entry({ externalId: 'new' }),
      entry({ externalId: 'done' }, { decision: 'done', decidedAt: now.toISOString(), snoozedUntil: null, appliedToSource: false }),
      entry({ externalId: 'soon' }, { decision: 'later', decidedAt: now.toISOString(), snoozedUntil: '2026-11-25T09:00:00Z', appliedToSource: false }),
      entry({ externalId: 'back' }, { decision: 'later', decidedAt: now.toISOString(), snoozedUntil: '2026-11-25T07:59:00Z', appliedToSource: false }),
      entry({ externalId: 'yesterday', kind: 'event', endsAt: '2026-11-24T18:00:00Z' })
    ];
    const dayStart = new Date('2026-11-25T00:00:00Z');
    expect(pendingCards(items, now, dayStart).map(x => x.item.externalId)).toEqual(['new', 'back']);
    expect(decisionCounts(items)).toEqual({ done: 1, not_done: 0, later: 2, dismissed: 0 });
  });

  it('applies a confirmed decision locally and never forgets a provider write', () => {
    const items = [entry({ externalId: 'a' }, { decision: 'done', decidedAt: '', snoozedUntil: null, appliedToSource: true })];
    const next = withDecision(items, items[0].key, 'later', '2026-11-25T10:00:00Z', false, now);
    expect(next[0].triage).toEqual({ decision: 'later', decidedAt: now.toISOString(),
      snoozedUntil: '2026-11-25T10:00:00Z', appliedToSource: true });
    expect(items[0].triage?.decision).toBe('done');
  });

  it('offers source completion only for tasks of a capable provider', () => {
    const providers = [{ id: 'microsoft' as const, displayName: 'M', configured: true, capabilities: ['tasks.complete'] }];
    expect(canApplyToSource(item({ kind: 'task' }), providers)).toBe(true);
    expect(canApplyToSource(item({ kind: 'mail' }), providers)).toBe(false);
    expect(canApplyToSource(item({ kind: 'task', provider: 'google' }), providers)).toBe(false);
  });

  it('labels time explicitly and in Italian', () => {
    const tz = 'Europe/Rome';
    expect(whenLabel(item({ kind: 'event', startsAt: '2026-11-25T09:00:00Z', endsAt: '2026-11-25T09:30:00Z' }), now, tz))
      .toBe('Oggi alle 10:00');
    expect(whenLabel(item({ kind: 'event', startsAt: '2026-11-25T07:30:00Z', endsAt: '2026-11-25T08:30:00Z' }), now, tz))
      .toBe('In corso, fino alle 09:30');
    expect(whenLabel(item({ kind: 'task', dueAt: '2026-11-24T23:59:59Z' }), now, tz)).toMatch(/^Scaduta/);
    expect(whenLabel(item({ kind: 'task' }), now, tz)).toBe('Senza scadenza');
    expect(whenLabel(item({ kind: 'mail', startsAt: '2026-11-25T06:15:00Z' }), now, tz)).toBe('Ricevuta alle 07:15');
  });

  it('opens only absolute https provider links', () => {
    expect(safeWebLink('https://outlook.office.com/mail/x')).toBe('https://outlook.office.com/mail/x');
    expect(safeWebLink('javascript:alert(1)')).toBeNull();
    expect(safeWebLink('http://example.test')).toBeNull();
    expect(safeWebLink('https://user:pass@example.test')).toBeNull();
    expect(safeWebLink('/relative')).toBeNull();
    expect(safeWebLink(null)).toBeNull();
  });
});

describe('connector and today API calls', () => {
  const cfg = { issuer: 'https://id.example.test/auth/realms/tao', clientId: 'tao-web', apiBaseUrl: 'https://tao.example.test' };

  it('sends decisions as JSON with explicit opt-in for provider writes', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ key: 'k', decision: 'done', snoozedUntil: null, appliedToSource: true }) });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => 'token');
    await api.decide('k', 'done', { applyToSource: true });
    await api.decide('k', 'later', { snoozeMinutes: 30 });
    expect(JSON.parse(fetcher.mock.calls[0][1].body)).toEqual({ key: 'k', decision: 'done', snoozeMinutes: null, applyToSource: true });
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ key: 'k', decision: 'later', snoozeMinutes: 30, applyToSource: false });
    expect(fetcher.mock.calls[0][0]).toBe('https://tao.example.test/api/v1/today/decisions');
  });

  it('undoes a decision by item key', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 204 });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => 'token');
    await api.undoDecision('microsoft:abc:mail:m|1');
    expect(fetcher.mock.calls[0][0]).toBe('https://tao.example.test/api/v1/today/decisions?key=microsoft%3Aabc%3Amail%3Am%7C1');
    expect(fetcher.mock.calls[0][1].method).toBe('DELETE');
  });

  it('requests the window as encoded UTC instants and validates ids before sending', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ items: [], accounts: [] }) });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => 'token');
    await api.today(new Date('2026-11-24T12:00:00Z'), new Date('2026-11-25T23:00:00Z'));
    expect(fetcher.mock.calls[0][0]).toBe('https://tao.example.test/api/v1/today?from=2026-11-24T12%3A00%3A00.000Z&to=2026-11-25T23%3A00%3A00.000Z');
    expect(() => api.disconnectConnector('../../privacy/erase')).toThrow();
    expect(() => api.authorizeConnector('dropbox' as never)).toThrow();
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('asks for the assistant with a dedicated purpose and the device time zone', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal('fetch', fetcher);
    const api = new MemoryApi(cfg, async () => 'token');
    await api.grantAssistantBriefing();
    await api.briefing(new Date('2026-11-24T12:00:00Z'), new Date('2026-11-25T23:00:00Z'), 'Europe/Rome');
    const grant = JSON.parse(fetcher.mock.calls[0][1].body);
    expect(fetcher.mock.calls[0][0]).toBe('https://tao.example.test/api/v1/consents');
    expect(grant).toMatchObject({ source: 'assistant', purpose: 'assistant.briefing', accepted: true });
    expect(fetcher.mock.calls[1][0]).toBe('https://tao.example.test/api/v1/today/briefing');
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
      from: '2026-11-24T12:00:00.000Z', to: '2026-11-25T23:00:00.000Z', timeZone: 'Europe/Rome'
    });
  });

  it('maps content-free API error codes to user messages', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 409, json: async () => ({ error: 'reconnect_required' }) }));
    const api = new MemoryApi(cfg, async () => 'token');
    await expect(api.today(now, now)).rejects.toMatchObject({ status: 409, code: 'reconnect_required',
      message: expect.stringContaining('ricollegare') });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 502, json: async () => ({ error: '<script>' }) }));
    await expect(api.today(now, now)).rejects.toMatchObject({ status: 502, code: undefined });
  });

  it('says that the daily AI budget is spent', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 429, json: async () => ({ error: 'assistant_budget_exceeded' }) }));
    const api = new MemoryApi(cfg, async () => 'token');
    await expect(api.briefing(now, now, 'Europe/Rome', 'it')).rejects.toMatchObject({
      status: 429, code: 'assistant_budget_exceeded', message: expect.stringContaining('limite giornaliero') });
  });
});
