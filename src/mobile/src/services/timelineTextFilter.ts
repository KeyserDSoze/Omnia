/**
 * Local view-only search over content already returned by signed/RLS pages.
 * No further fetch, persistence, reordering or canonical raw mutation.
 */
function foldForLoadedSearch(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase('it-IT').replace(/\s+/gu, ' ').trim();
}

export function filterLoadedByText<T extends { content: string }>(
  items: readonly T[], query: string | null
): T[] {
  const needle = foldForLoadedSearch(query ?? '');
  return needle.length === 0
    ? [...items]
    : items.filter(item => foldForLoadedSearch(item.content).includes(needle));
}
