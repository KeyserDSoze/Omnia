import { describe, it, expect, afterEach, vi } from 'vitest';
import { classifyMemo, normalizeMemo } from '../../src/mobile/src/domain/tags';

describe('classificazione deterministica prima del modello AI', () => {
  it('riconosce i domini di lavoro e relazioni', () => {
    expect(classifyMemo('Riunione con un amico e cliente')).toEqual(['lavoro', 'relazioni']);
  });
  it('preserva la fonte e il testo originale normalizzato', () => {
    const memo = normalizeMemo('  Incontro con un cliente  ', '2026-11-17T10:00:00Z');
    expect(memo.source).toBe('manual');
    expect(memo.content).toBe('Incontro con un cliente');
    expect(memo.tags).toContain('lavoro');
  });
  it('rifiuta contenuti vuoti', () => {
    expect(() => normalizeMemo(' ', '2026-11-17T10:00:00Z')).toThrow();
  });
});

afterEach(() => vi.unstubAllGlobals());

describe('capture source key collision regression #177', () => {
  it('creates different source keys for independent captures with an identical timestamp', () => {
    const occurredAt = '2026-11-22T20:00:00.000Z';
    let counter = 0;
    vi.stubGlobal('crypto', {
      randomUUID: () => '00000000-0000-4000-8000-' + String(++counter).padStart(12, '0')
    });
    const first = normalizeMemo('Primo evento distinto', occurredAt, true);
    const second = normalizeMemo('Secondo evento distinto', occurredAt, true);
    expect(first.occurredAt).toBe(second.occurredAt);
    expect(first.externalId).not.toBe(second.externalId);
    expect(first.externalId).toMatch(/^local-[a-f0-9-]{36}$/);
    expect(second.externalId).toMatch(/^local-[a-f0-9-]{36}$/);
    // Replay must use the original normalized entry, not regenerate a key.
    const retried = { ...first };
    expect(retried.externalId).toBe(first.externalId);
  });

  it('rejects cloud capture when secure client randomness is unavailable', () => {
    vi.stubGlobal('crypto', undefined);
    expect(() => normalizeMemo('Non sincronizzare', '2026-11-22T20:00:00.000Z', true))
      .toThrow('Identificativi sicuri non disponibili');
  });

  it('allows two distinct local demo entries with a fixed timestamp without crypto', () => {
    vi.stubGlobal('crypto', undefined);
    const occurredAt = '2026-11-22T20:00:00.000Z';
    const first = normalizeMemo('demo uno', occurredAt);
    const second = normalizeMemo('demo due', occurredAt);
    expect(first.externalId).not.toBe(second.externalId);
  });
});
