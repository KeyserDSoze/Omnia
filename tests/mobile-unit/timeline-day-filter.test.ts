import { describe, expect, it } from 'vitest';
import { dayOptions, filterLoadedByLocalDay } from '../../src/mobile/src/services/timelineDayFilter';
import { filterLoadedMemories } from '../../src/mobile/src/services/timelineSourceFilter';
import { filterLoadedByTag } from '../../src/mobile/src/services/timelineTagFilter';
import { groupTimelineByLocalDay } from '../../src/mobile/src/services/timelineDayGrouping';

type Row = { id: string; source: string; tags: string[]; occurredAt: string };
const row = (id: string, occurredAt: string, source = 'a', tags = ['lavoro']): Row =>
  ({ id, occurredAt, source, tags });
const items: Row[] = [
  row('new', '2035-06-21T17:30:00Z'),
  row('same', '2035-06-21T13:30:00Z'),
  row('older', '2035-06-20T13:30:00Z', 'b', ['relazioni'])
];

describe('loaded-memory local calendar-day selector #185', () => {
  it('offers unique ordered local day keys and Italian labels from all loaded rows', () => {
    expect(dayOptions(items, 'Europe/Rome')).toEqual([
      { key: '2035-06-21', label: expect.stringContaining('21 giugno 2035') },
      { key: '2035-06-20', label: expect.stringContaining('20 giugno 2035') }
    ]);
    expect(dayOptions(items.slice(0, 1), 'Europe/Rome')).toHaveLength(1);
    expect(dayOptions([], 'UTC')).toEqual([]);
    expect(dayOptions([...items, items[0]], 'Europe/Rome')).toHaveLength(2);
  });

  it('must use the original instant in the selected timezone around Rome DST/midnight', () => {
    const boundary = [
      row('30', '2026-03-29T22:30:00.000Z'),
      row('29', '2026-03-29T00:30:00.000Z'),
      row('29b', '2026-03-28T23:30:00.000Z')
    ];
    expect(dayOptions(boundary, 'Europe/Rome').map(x => x.key))
      .toEqual(['2026-03-30', '2026-03-29']);
    expect(filterLoadedByLocalDay(boundary, '2026-03-29', 'Europe/Rome')
      .map(x => x.id)).toEqual(['29', '29b']);
    expect(dayOptions(boundary, 'America/Los_Angeles').map(x => x.key))
      .toEqual(['2026-03-29', '2026-03-28']);
    expect(filterLoadedByLocalDay(boundary, '2026-03-29', 'America/Los_Angeles')
      .map(x => x.id)).toEqual(['30']);
    expect(filterLoadedByLocalDay(boundary, '2026-03-29', 'UTC')
      .map(x => x.id)).toEqual(['30', '29']);
  });

  it('combines day AND source AND tag, preserving chronological rows and no stale headings', () => {
    const snapshot = structuredClone(items);
    const filtered = filterLoadedByLocalDay(
      filterLoadedByTag(filterLoadedMemories(items, 'a'), 'lavoro'),
      '2035-06-20', 'UTC');
    expect(filtered).toEqual([]);
    expect(groupTimelineByLocalDay(filtered, 'UTC')).toEqual([]);
    const correct = filterLoadedByLocalDay(
      filterLoadedByTag(filterLoadedMemories(items, 'b'), 'relazioni'),
      '2035-06-20', 'UTC');
    expect(correct.map(x => x.id)).toEqual(['older']);
    expect(items).toEqual(snapshot);
    expect(filterLoadedByLocalDay(items, null, 'UTC')[0]).toBe(items[0]);
    expect(filterLoadedByLocalDay(items, 'not-an-existing-day', 'UTC')).toEqual([]);
  });

  it('older pages introduce choices, do not duplicate existing days, and never fetch anything', () => {
    const first = items.slice(0, 1);
    const combined = [...first, ...items.slice(1)];
    expect(dayOptions(first, 'UTC').map(x => x.key)).toEqual(['2035-06-21']);
    expect(dayOptions(combined, 'UTC').map(x => x.key))
      .toEqual(['2035-06-21', '2035-06-20']);
    expect(filterLoadedByLocalDay(combined, '2035-06-21', 'UTC')
      .map(x => x.id)).toEqual(['new', 'same']);
    expect(combined.map(x => x.id)).toEqual(['new', 'same', 'older']);
  });

  it('does not invent a date from invalid signed provenance metadata', () => {
    expect(() => dayOptions([row('broken', 'not-an-ISO-instant')], 'UTC'))
      .toThrow(RangeError);
  });
});
