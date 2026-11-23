import { describe, expect, it } from 'vitest';
import { filterLoadedByText } from '../../src/mobile/src/services/timelineTextFilter';
import { filterLoadedMemories } from '../../src/mobile/src/services/timelineSourceFilter';
import { filterLoadedByTag } from '../../src/mobile/src/services/timelineTagFilter';
import { filterLoadedByLocalDay } from '../../src/mobile/src/services/timelineDayFilter';
import { groupTimelineByLocalDay } from '../../src/mobile/src/services/timelineDayGrouping';

type Row = { id: string; source: string; tags: string[]; occurredAt: string; summary: string };
const rows: Row[] = [
  { id: '1', source: 'manual', tags: ['lavoro'], occurredAt: '2035-06-21T12:00:00Z', summary: 'Caffè  Riunione con cliente' },
  { id: '2', source: 'manual', tags: ['relazioni'], occurredAt: '2035-06-21T11:00:00Z', summary: 'Famiglia e amici' },
  { id: '3', source: 'import', tags: ['lavoro'], occurredAt: '2035-06-20T10:00:00Z', summary: 'CAFFE\u0300 riunione e report' }
];

describe('loaded-only signed timeline text filter #187', () => {
  it('matches only visible summary with case/accents/Unicode/whitespace normalized', () => {
    expect(filterLoadedByText(rows, '  cAfFe     rIuNiOnE  ').map(x => x.id)).toEqual(['1', '3']);
    expect(filterLoadedByText(rows, 'famiglia').map(x => x.id)).toEqual(['2']);
    expect(filterLoadedByText(rows, 'non presente')).toEqual([]);
    expect(filterLoadedByText(rows, 'manual')).toEqual([]);
  });

  it('keeps ordering, row identity and input objects unchanged on blank query', () => {
    const before = structuredClone(rows);
    const all = filterLoadedByText(rows, '   ');
    expect(all).toEqual(rows);
    expect(all).not.toBe(rows);
    expect(all[0]).toBe(rows[0]);
    expect(rows).toEqual(before);
    expect(filterLoadedByText([], 'caffe')).toEqual([]);
  });

  it('intersects with source, tags and local date without creating phantom headings', () => {
    const afterAll = filterLoadedByText(filterLoadedByLocalDay(
      filterLoadedByTag(filterLoadedMemories(rows, 'manual'), 'lavoro'),
      '2035-06-21', 'UTC'), '  CAFFE ');
    expect(afterAll.map(x => x.id)).toEqual(['1']);
    expect(groupTimelineByLocalDay(afterAll, 'UTC')).toHaveLength(1);
    expect(filterLoadedByText(filterLoadedMemories(rows, 'manual'), 'report')).toEqual([]);
    expect(groupTimelineByLocalDay(filterLoadedByText(rows, 'not found'), 'UTC')).toEqual([]);
  });

  it('new explicit keyset pages extend search results without fetching or deduplicating in helper', () => {
    expect(filterLoadedByText(rows.slice(0, 2), 'riunione').map(x => x.id)).toEqual(['1']);
    expect(filterLoadedByText(rows, 'riunione').map(x => x.id)).toEqual(['1', '3']);
    expect(rows.map(x => x.id)).toEqual(['1', '2', '3']);
  });
});
