/**
 * Tag selectors are view-only projections of memory tags already returned by
 * the actor-scoped signed PostgreSQL API. Tags are currently deterministic
 * keyword heuristics, not AI conclusions or connector permissions.
 */
export function tagOptions<T extends { tags: readonly string[] }>(
  items: readonly T[]
): string[] {
  return [...new Set(items.flatMap(item => item.tags))].sort();
}

export function filterLoadedByTag<T extends { tags: readonly string[] }>(
  items: readonly T[], tag: string | null
): T[] {
  return tag === null ? [...items] : items.filter(item => item.tags.includes(tag));
}
