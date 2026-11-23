import { describe, expect, it } from 'vitest';
import { filterLoadedMemories, sourceOptions } from '../../src/mobile/src/services/timelineSourceFilter';

type Row = { id: string; source: string; summary: string };
const rows: Row[] = [
  { id: '1', source: 'manual', summary: 'Note one' },
  { id: '2', source: 'crm', summary: 'Note two' },
  { id: '3', source: 'manual', summary: 'Note three' },
  { id: '4', source: 'crm-dev', summary: 'Note four' }
];

describe('authenticated loaded timeline source filter #182', () => {
  it('offers unique sorted canonical source options, not excerpts or tag names', () => {
    expect(sourceOptions(rows)).toEqual(['crm', 'crm-dev', 'manual']);
    expect(sourceOptions([])).toEqual([]);
    expect(sourceOptions([{ id: 'x', source: 'crm', summary: '' }, ...rows]))
      .toEqual(['crm', 'crm-dev', 'manual']);
  });

  it('shows all loaded notes by default and only exact source matches when selected', () => {
    expect(filterLoadedMemories(rows, null)).toEqual(rows);
    expect(filterLoadedMemories(rows, 'crm').map(x => x.id)).toEqual(['2']);
    expect(filterLoadedMemories(rows, 'manual').map(x => x.id)).toEqual(['1', '3']);
    expect(filterLoadedMemories(rows, 'CRM')).toEqual([]);
    expect(filterLoadedMemories(rows, 'missing')).toEqual([]);
  });

  it('does not mutate canonical pages and includes older loaded rows without new network requests', () => {
    const existing = structuredClone(rows);
    const first = filterLoadedMemories(rows.slice(0, 2), 'manual');
    expect(first.map(x => x.id)).toEqual(['1']);
    const withOlderPage = filterLoadedMemories(rows, 'manual');
    expect(withOlderPage.map(x => x.id)).toEqual(['1', '3']);
    expect(rows).toEqual(existing);
  });
});
