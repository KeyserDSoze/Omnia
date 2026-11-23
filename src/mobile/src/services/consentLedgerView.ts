import type { ConsentDto, ConsentPageDto, MemoryApi } from './memoryApi';

/**
 * View-only session generation gate for paginated, private consent history.
 * An invalidated response or error may NEVER recreate a former actor's grants.
 * JWT, server-side consent guards and FORCE RLS remain the real authority.
 */
export class ConsentLedgerEpoch {
  private generation = 0;

  invalidate(): void {
    this.generation++;
  }

  async refresh(api: Pick<MemoryApi, 'consentPage'>,
    apply: (page: ConsentPageDto) => void): Promise<boolean> {
    const generation = ++this.generation;
    try {
      const page = await api.consentPage();
      if (generation !== this.generation) return false;
      apply(page);
      return true;
    } catch (error) {
      if (generation !== this.generation) return false;
      throw error;
    }
  }

  async append(api: Pick<MemoryApi, 'consentPage'>, cursor: string,
    apply: (page: ConsentPageDto) => void): Promise<boolean> {
    const generation = this.generation;
    try {
      const page = await api.consentPage(cursor);
      if (generation !== this.generation) return false;
      apply(page);
      return true;
    } catch (error) {
      if (generation !== this.generation) return false;
      throw error;
    }
  }
}

/** A grant UUID must not duplicate at a page boundary after concurrent writes. */
export function appendUniqueConsents(
  current: readonly ConsentDto[], older: readonly ConsentDto[]
): ConsentDto[] {
  const seen = new Set(current.map(item => item.id));
  const result = [...current];
  for (const item of older) {
    if (seen.has(item.id)) continue;
    seen.add(item.id);
    result.push(item);
  }
  return result;
}
