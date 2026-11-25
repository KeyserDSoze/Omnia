import type { ConnectorProviderDto, TodayItemDto, TriageDecision, WorkItemDto, WorkItemKind } from './memoryApi';
import { intlTag, t } from '../i18n';

/**
 * The daily deck is a VIEW over live provider signals. The order comes from the server's
 * deterministic priority (no AI); the client only hides what the user already decided.
 */
export type DeckWindow = { from: Date; to: Date; dayStart: Date };

const RECENT_HOURS = 12;
const QUEUE_HOURS = 48;
export const TOMORROW_HOUR = 8;

/**
 * Local calendar day (DST-safe). It reaches back two days so a card queued for tomorrow can still
 * be fetched; undecided mail older than the previous evening is filtered out by pendingCards.
 */
export function deckWindow(now: Date): DeckWindow {
  const dayStart = new Date(now);
  dayStart.setHours(0, 0, 0, 0);
  const to = new Date(dayStart);
  to.setDate(to.getDate() + 1);
  return { from: new Date(dayStart.getTime() - QUEUE_HOURS * 3600_000), to, dayStart };
}

/**
 * Undecided cards, plus "later" cards whose snooze has expired (they come back in the queue).
 * Undecided mail and chats older than the previous evening are noise, but a queued one is not.
 */
export function pendingCards(items: readonly TodayItemDto[], now: Date, dayStart: Date): TodayItemDto[] {
  const recentFrom = dayStart.getTime() - RECENT_HOURS * 3600_000;
  return items.filter(entry => {
    const { item, triage } = entry;
    if (item.kind === 'event' && item.endsAt && new Date(item.endsAt) < dayStart) return false;
    if (!triage) {
      const stale = (item.kind === 'mail' || item.kind === 'chat') && item.startsAt !== null &&
        new Date(item.startsAt).getTime() < recentFrom;
      return !stale;
    }
    return triage.decision === 'later' && triage.snoozedUntil !== null &&
      new Date(triage.snoozedUntil) <= now;
  });
}

/** Minutes from now until the next local morning ("queue for tomorrow"). */
export function tomorrowSnoozeMinutes(now: Date, hour: number = TOMORROW_HOUR): number {
  const target = new Date(now);
  target.setDate(target.getDate() + 1);
  target.setHours(hour, 0, 0, 0);
  return Math.min(2880, Math.max(15, Math.ceil((target.getTime() - now.getTime()) / 60_000)));
}

export type DeckAction = TriageDecision | 'tomorrow' | 'undo';

/** Keyboard shortcuts of the card; any modifier leaves the key to the browser. */
export function deckShortcut(event: { key: string; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean }): DeckAction | null {
  if (event.ctrlKey || event.metaKey || event.altKey) return null;
  switch (event.key) {
    case 'ArrowRight': return 'done';
    case 'ArrowLeft': return 'not_done';
    case 'ArrowDown': return 'later';
    case 'ArrowUp': return 'tomorrow';
    case 'Delete':
    case 'Backspace': return 'dismissed';
    case 'z':
    case 'Z': return 'undo';
    default: return null;
  }
}

/** The card goes back in the deck: only the confirmed server undo may call this. */
export function withoutDecision(items: readonly TodayItemDto[], key: string): TodayItemDto[] {
  return items.map(entry => entry.key !== key ? entry : { ...entry, triage: null });
}

export function decisionCounts(items: readonly TodayItemDto[]): Record<TriageDecision, number> {
  const counts: Record<TriageDecision, number> = { done: 0, not_done: 0, later: 0, dismissed: 0 };
  for (const entry of items) if (entry.triage) counts[entry.triage.decision]++;
  return counts;
}

/** Applies a confirmed server decision locally, without refetching provider data. */
export function withDecision(items: readonly TodayItemDto[], key: string, decision: TriageDecision,
  snoozedUntil: string | null, appliedToSource: boolean, now: Date): TodayItemDto[] {
  return items.map(entry => entry.key !== key ? entry : {
    ...entry,
    triage: {
      decision, decidedAt: now.toISOString(), snoozedUntil,
      appliedToSource: appliedToSource || entry.triage?.appliedToSource === true
    }
  });
}

/** Only tasks of a provider that declares the capability can be completed at the source. */
export function canApplyToSource(item: WorkItemDto, providers: readonly ConnectorProviderDto[]): boolean {
  return item.kind === 'task' &&
    providers.some(p => p.id === item.provider && p.capabilities.includes('tasks.complete'));
}

export const kindLabel = (kind: WorkItemKind): string => t(`kind.${kind}`);

export const providerTaskApp: Record<string, string> = {
  microsoft: 'Microsoft To Do', google: 'Google Tasks'
};

export function whenLabel(item: WorkItemDto, now: Date, timeZone?: string): string {
  const time = (iso: string) => new Date(iso).toLocaleTimeString(intlTag(),
    { hour: '2-digit', minute: '2-digit', timeZone });
  const day = (iso: string) => new Date(iso).toLocaleDateString(intlTag(),
    { day: 'numeric', month: 'short', timeZone });
  const sameDay = (iso: string) => day(iso) === day(now.toISOString());
  switch (item.kind) {
    case 'event':
      if (!item.startsAt) return t('when.event');
      if (item.endsAt && new Date(item.endsAt) <= now) return t('when.ended', { time: time(item.endsAt) });
      if (new Date(item.startsAt) <= now)
        return item.endsAt ? t('when.ongoingUntil', { time: time(item.endsAt) }) : t('when.ongoing');
      return sameDay(item.startsAt) ? t('when.todayAt', { time: time(item.startsAt) })
        : t('when.dayAt', { day: day(item.startsAt), time: time(item.startsAt) });
    case 'task':
      if (!item.dueAt) return t('when.noDue');
      if (new Date(item.dueAt) < now) return t('when.overdue', { day: day(item.dueAt) });
      return sameDay(item.dueAt) ? t('when.dueToday') : t('when.dueOn', { day: day(item.dueAt) });
    case 'mail':
      if (!item.startsAt) return t('kind.mail');
      return sameDay(item.startsAt) ? t('when.receivedAt', { time: time(item.startsAt) })
        : t('when.receivedOn', { day: day(item.startsAt), time: time(item.startsAt) });
    case 'chat':
      return item.startsAt ? t('when.chatAt', { time: time(item.startsAt) }) : t('kind.chat');
  }
}

/** Provider links are untrusted data: only absolute https URLs are ever opened. */
export function safeWebLink(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' && parsed.username === '' && parsed.password === '' ? parsed.href : null;
  } catch {
    return null;
  }
}
