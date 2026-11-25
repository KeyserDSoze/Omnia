import type { ConnectorProviderDto, TodayItemDto, TriageDecision, WorkItemDto, WorkItemKind } from './memoryApi';
import { intlTag, t } from '../i18n';

/**
 * The daily deck is a VIEW over live provider signals. The order comes from the server's
 * deterministic priority (no AI); the client only hides what the user already decided.
 */
export type DeckWindow = { from: Date; to: Date; dayStart: Date };

/** Local calendar day (DST-safe) plus the previous evening, so late mail is not lost. */
export function deckWindow(now: Date): DeckWindow {
  const dayStart = new Date(now);
  dayStart.setHours(0, 0, 0, 0);
  const to = new Date(dayStart);
  to.setDate(to.getDate() + 1);
  return { from: new Date(dayStart.getTime() - 12 * 3600_000), to, dayStart };
}

/** Undecided cards, plus "later" cards whose snooze has expired (they come back in the queue). */
export function pendingCards(items: readonly TodayItemDto[], now: Date, dayStart: Date): TodayItemDto[] {
  return items.filter(entry => {
    const { item, triage } = entry;
    if (item.kind === 'event' && item.endsAt && new Date(item.endsAt) < dayStart) return false;
    if (!triage) return true;
    return triage.decision === 'later' && triage.snoozedUntil !== null &&
      new Date(triage.snoozedUntil) <= now;
  });
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
