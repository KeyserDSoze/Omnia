/**
 * A calendar day in the device's local timezone, derived from the immutable
 * occurredAt instant returned by the signed API (or local demo capture).
 * This is a view transform only: never changes keyset order or SQL/consent.
 */
import { intlTag } from '../i18n';
export type TimelineDayGroup<T> = {
  key: string;
  label: string;
  items: T[];
};

export function groupTimelineByLocalDay<T extends { occurredAt: string }>(
  items: readonly T[], timeZone?: string
): TimelineDayGroup<T>[] {
  if (items.length === 0) return [];
  const dayFormatter = new Intl.DateTimeFormat('en-GB', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit'
  });
  const labelFormatter = new Intl.DateTimeFormat(intlTag(), {
    timeZone, weekday: 'long', day: 'numeric', month: 'long', year: 'numeric'
  });
  const days = new Map<string, TimelineDayGroup<T>>();
  for (const item of items) {
    const instant = new Date(item.occurredAt);
    if (!Number.isFinite(instant.getTime()))
      throw new RangeError('Invalid timeline event instant.');

    // Date#toISOString().slice(0, 10) would group by UTC day, which can
    // differ from the user's calendar day around midnight and DST.
    const parts = dayFormatter.formatToParts(instant);
    const year = parts.find(p => p.type === 'year')?.value;
    const month = parts.find(p => p.type === 'month')?.value;
    const day = parts.find(p => p.type === 'day')?.value;
    if (!year || !month || !day)
      throw new RangeError('Missing calendar date parts.');
    const key = year + '-' + month + '-' + day;
    let group = days.get(key);
    if (!group) {
      group = { key, label: labelFormatter.format(instant), items: [] };
      days.set(key, group);
    }
    group.items.push(item);
  }
  // Map preserves first appearance order; pages arrive ordered by
  // occurredAt DESC/id DESC. Adjacent keyset pages extend existing days.
  return [...days.values()];
}
