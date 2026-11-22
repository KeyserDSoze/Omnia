import type { ConsentDto, MemoryApi, RemoteMemory } from './memoryApi';

/**
 * A view-only generation gate for signed web session responses.
 *
 * Every latest refresh owns a new generation. Logout, revocation, new grant
 * and erasure invalidate ALL earlier reads synchronously. The gate does not
 * persist data or make authentication decisions; API/RLS remain authoritative.
 */
export class CloudViewEpoch {
  private generation = 0;

  invalidate(): void {
    this.generation++;
  }

  async refresh(
    api: Pick<MemoryApi, 'listConsents' | 'recent'>,
    apply: (consents: ConsentDto[], recent: RemoteMemory[]) => void
  ): Promise<boolean> {
    const generation = ++this.generation;
    try {
      const [consents, recent] = await Promise.all([api.listConsents(), api.recent()]);
      if (generation !== this.generation) return false;
      apply(consents, recent);
      return true;
    } catch (error) {
      // Late failures must not overwrite the logout/erasure status either.
      if (generation !== this.generation) return false;
      throw error;
    }
  }
}
