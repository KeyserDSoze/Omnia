import { describe, expect, it } from 'vitest';
import { filterLoadedMemories, sourceOptions } from '../../src/mobile/src/services/timelineSourceFilter';
import { tagOptions, filterLoadedByTag } from '../../src/mobile/src/services/timelineTagFilter';
import { groupTimelineByLocalDay } from '../../src/mobile/src/services/timelineDayGrouping';

type Memo = {
  id: string;
  source: string;
  tags: string[];
  occurredAt: string;
};
const rows: Memo[] = [
  { id: 'a', source: 'alpha', tags: ['lavoro', 'personale'], occurredAt: '2033-06-21T12:00:00Z' },
  { id: 'b', source: 'beta', tags: ['relazioni'], occurredAt: '2033-06-21T11:00:00Z' },
  { id: 'c', source: 'alpha', tags: ['lavoro'], occurredAt: '2033-06-20T12:00:00Z' },
  { id: 'd', source: 'beta', tags: ['relazioni', 'lavoro'], occurredAt: '2033-06-19T12:00:00Z' }
];

describe('loaded-page Web tag filter #184', () => {
  it('provides canonical unique tag options independently of source selection', () => {
    expect(tagOptions(rows)).toEqual(['lavoro', 'personale', 'relazioni']);
    expect(tagOptions([])).toEqual([]);
    expect(tagOptions([...rows, rows[0]])).toEqual(['lavoro', 'personale', 'relazioni']);
    expect(tagOptions(filterLoadedMemories(rows, 'alpha'))).toEqual(['lavoro', 'personale']);
    // In the UI the chooser MUST use all loaded rows, not only the source-filtered view:
    expect(tagOptions(rows)).toContain('relazioni');
  });

  it('filters by exact array membership with no substring matching or implicit normalization', () => {
    expect(filterLoadedByTag(rows, null)).toEqual(rows);
    expect(filterLoadedByTag(rows, 'lavoro').map(x => x.id)).toEqual(['a', 'c', 'd']);
    expect(filterLoadedByTag(rows, 'relazioni').map(x => x.id)).toEqual(['b', 'd']);
    expect(filterLoadedByTag(rows, 'LAVORO')).toEqual([]);
    expect(filterLoadedByTag(rows, 'lav')).toEqual([]);
    expect(filterLoadedByTag(rows, 'missing')).toEqual([]);
  });

  it('combines source AND tag, not OR, and keeps only headings for visible records', () => {
    const alpha = filterLoadedMemories(rows, 'alpha');
    const combined = filterLoadedByTag(alpha, 'relazioni');
    expect(combined).toEqual([]);
    expect(groupTimelineByLocalDay(combined, 'UTC')).toEqual([]);

    const beta = filterLoadedByTag(filterLoadedMemories(rows, 'beta'), 'relazioni');
    expect(beta.map(x => x.id)).toEqual(['b', 'd']);
    expect(groupTimelineByLocalDay(beta, 'UTC').map(x => x.key))
      .toEqual(['2033-06-21', '2033-06-19']);
    expect(sourceOptions(beta)).toEqual(['beta']);
  });

  it('old-page append reveals an additional tag without mutating already loaded rows', () => {
    const first = rows.slice(0, 1);
    const older = rows.slice(1);
    const pristine = structuredClone(rows);
    expect(tagOptions(first)).toEqual(['lavoro', 'personale']);
    const allLoaded = [...first, ...older];
    expect(tagOptions(allLoaded)).toEqual(['lavoro', 'personale', 'relazioni']);
    expect(filterLoadedByTag(allLoaded, 'relazioni').map(x => x.id)).toEqual(['b', 'd']);
    expect(rows).toEqual(pristine);
    expect(filterLoadedByTag(allLoaded, null)[0]).toBe(rows[0]);
  });
});
