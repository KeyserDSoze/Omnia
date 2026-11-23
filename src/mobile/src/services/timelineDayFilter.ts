import { groupTimelineByLocalDay, type TimelineDayGroup } from './timelineDayGrouping';

/**
 * The device-local day selector is purely a view of already-loaded, JWT/RLS
 * scoped occurredAt instants. Never infer source timezone or global results.
 * Both this chooser and the headings reuse precisely the same Intl day logic.
 */
export type LoadedDayOption = Pick<TimelineDayGroup<never>, 'key' | 'label'>;

export function dayOptions<T extends { occurredAt: string }>(
  items: readonly T[], timeZone?: string
): LoadedDayOption[] {
  return groupTimelineByLocalDay(items, timeZone).map(
    ({ key, label }) => ({ key, label }));
}

/** Selection is an AND with source/tag; no reordering or extra API call. */
export function filterLoadedByLocalDay<T extends { occurredAt: string }>(
  items: readonly T[], localDay: string | null, timeZone?: string
): T[] {
  if (localDay === null) return [...items];
  return groupTimelineByLocalDay(items, timeZone)
    .find(group => group.key === localDay)?.items ?? [];
}
