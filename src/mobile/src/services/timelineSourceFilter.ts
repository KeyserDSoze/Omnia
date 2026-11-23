/**
 * A *loaded-page only* view transform. This does not query PostgreSQL,
 * collect more pages, persist state or claim global source search.
 * The signed API/forced RLS remain the exclusive source of notes.
 */
export function sourceOptions<T extends { source: string }>(items: readonly T[]): string[] {
  return [...new Set(items.map(item => item.source))].sort();
}

export function filterLoadedMemories<T extends { source: string }>(
  items: readonly T[], source: string | null
): T[] {
  return source === null ? [...items] : items.filter(item => item.source === source);
}
