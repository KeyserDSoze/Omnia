import { describe, it, expect } from 'vitest';
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
