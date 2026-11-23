import { describe, expect, it } from 'vitest';
import { groupTimelineByLocalDay } from '../../src/mobile/src/services/timelineDayGrouping';
import { filterLoadedMemories } from '../../src/mobile/src/services/timelineSourceFilter';

type Memo = { id: string; source: string; occurredAt: string };
const row = (id: string, time: string, source = 'manual'): Memo =>
  ({ id, source, occurredAt: time });

describe('user-local calendar days over signed-in loaded timeline #183', () => {
  it('uses local dates, not UTC or naive 24-hour bins, at the Europe/Rome DST boundary', () => {
    const entries = [
      row('newer', '2026-03-29T22:30:00.000Z'),
      row('same', '2026-03-29T00:30:00.000Z'),
      row('older', '2026-03-28T23:30:00.000Z')
    ];
    const rome = groupTimelineByLocalDay(entries, 'Europe/Rome');
    expect(rome.map(day => day.key)).toEqual(['2026-03-30', '2026-03-29']);
    expect(rome.map(day => day.label)).toEqual([
      'lunedì 30 marzo 2026', 'domenica 29 marzo 2026'
    ]);
    expect(rome.map(day => day.items.map(item => item.id))).toEqual([
      ['newer'], ['same', 'older']
    ]);
    const losAngeles = groupTimelineByLocalDay(entries, 'America/Los_Angeles');
    expect(losAngeles.map(day => day.key)).toEqual(['2026-03-29', '2026-03-28']);
    expect(losAngeles.map(day => day.items.map(item => item.id))).toEqual([
      ['newer'], ['same', 'older']
    ]);
  });

  it('extends a single day instead of duplicating headings when older pages arrive', () => {
    const firstPage = [
      row('a', '2026-11-23T10:40:00.000Z'),
      row('b', '2026-11-23T10:30:00.000Z')
    ];
    const olderPage = [
      row('c', '2026-11-23T09:40:00.000Z'),
      row('d', '2026-11-22T21:00:00.000Z')
    ];
    const saved = structuredClone([...firstPage, ...olderPage]);
    expect(groupTimelineByLocalDay(firstPage, 'UTC').map(x => x.items.length))
      .toEqual([2]);
    const merged = groupTimelineByLocalDay([...firstPage, ...olderPage], 'UTC');
    expect(merged.map(x => x.key)).toEqual(['2026-11-23', '2026-11-22']);
    expect(merged.map(x => x.items.map(y => y.id))).toEqual([
      ['a', 'b', 'c'], ['d']
    ]);
    expect([...firstPage, ...olderPage]).toEqual(saved);
    expect(merged[0].items[0]).toBe(firstPage[0]);
  });

  it('groups only visible source-filtered rows, without residual headings from another source', () => {
    const entries = [
      row('a', '2026-05-20T12:00:00Z', 'manual'),
      row('b', '2026-05-19T12:00:00Z', 'crm'),
      row('c', '2026-05-18T12:00:00Z', 'manual')
    ];
    const filtered = groupTimelineByLocalDay(
      filterLoadedMemories(entries, 'manual'), 'UTC');
    expect(filtered.map(x => x.key)).toEqual(['2026-05-20', '2026-05-18']);
    expect(groupTimelineByLocalDay([], 'UTC')).toEqual([]);
    expect(entries.length).toBe(3);
  });

  it('fails rather than silently inventing a day for invalid provenance time', () => {
    expect(() => groupTimelineByLocalDay([row('bad', 'not-an-instant')], 'UTC'))
      .toThrow(RangeError);
  });
});
