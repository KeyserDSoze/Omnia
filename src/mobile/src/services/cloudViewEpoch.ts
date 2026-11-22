import type { ConsentDto, MemoryApi, MemoryPageDto } from './memoryApi';

/**
 * A view-only generation gate for signed Web cloud timeline and pages.
 *
 * Every latest refresh owns a new generation. Logout, revocation, new grant
 * and erasure invalidate earlier reads and page appends synchronously.
 * Authentication and PostgreSQL RLS remain the authoritative boundary.
 */
export class CloudViewEpoch {
  private generation = 0;

  invalidate(): void {
    this.generation++;
  }

  async refresh(
    api: Pick<MemoryApi, 'listConsents' | 'page'>,
    apply: (consents: ConsentDto[], page: MemoryPageDto) => void
  ): Promise<boolean> {
    const generation = ++this.generation;
    try {
      const [consents, page] = await Promise.all([api.listConsents(), api.page()]);
      if (generation !== this.generation) return false;
      apply(consents, page);
      return true;
    } catch (error) {
      if (generation !== this.generation) return false;
      throw error;
    }
  }

  async append(
    api: Pick<MemoryApi, 'page'>,
    cursor: string,
    apply: (page: MemoryPageDto) => void
  ): Promise<boolean> {
    const generation = this.generation;
    try {
      const page = await api.page(cursor);
      if (generation !== this.generation) return false;
      apply(page);
      return true;
    } catch (error) {
      // A page response/error that arrives after refresh/logout/erase
      // cannot apply to a different timeline or overwrite current notices.
      if (generation !== this.generation) return false;
      throw error;
    }
  }
}

/** Keep canonical UUIDs unique if a new capture crosses a page boundary. */
export function appendUniqueById<T extends { id: string }>(
  current: readonly T[], older: readonly T[]
): T[] {
  const known = new Set(current.map(row => row.id));
  const result = [...current];
  for (const item of older) {
    if (known.has(item.id)) continue;
    known.add(item.id);
    result.push(item);
  }
  return result;
}
