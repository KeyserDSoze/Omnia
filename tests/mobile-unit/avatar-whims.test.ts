import { describe, expect, it } from 'vitest';
import { nextWhim, smokeDelays, smokeMs, smokeUnits, type Whim } from '../../src/mobile/src/components/effects';

const sequence = (...values: number[]) => { let i = 0; return () => values[i++ % values.length]; };

describe('avatar whims', () => {
  it('waits between 4 and 11 seconds', () => {
    expect(nextWhim(sequence(0.5, 0)).waitMs).toBe(4000);
    expect(nextWhim(sequence(0.5, 0.999999)).waitMs).toBeGreaterThan(10_990);
    expect(nextWhim(sequence(0.5, 0.999999)).waitMs).toBeLessThan(11_000);
  });

  it('can pick every whim', () => {
    const seen = new Set<Whim>();
    for (let r = 0; r < 1; r += 0.05) seen.add(nextWhim(sequence(r, 0.5)).kind);
    expect([...seen].sort()).toEqual(['burst', 'flip', 'reverse', 'tumble']);
  });

  it('never repeats the previous whim', () => {
    for (const previous of ['reverse', 'flip', 'tumble', 'burst'] as const)
      for (let r = 0; r < 1; r += 0.05) expect(nextWhim(sequence(r, 0.5), previous).kind).not.toBe(previous);
  });
});

describe('smoke reveal', () => {
  it('staggers the units and stays inside a bounded time', () => {
    const delays = smokeDelays(5);
    expect(delays).toHaveLength(5);
    expect(delays[0]).toBe(0);
    expect([...delays].sort((a, b) => a - b)).toEqual(delays);
    const long = smokeDelays(400);
    expect(long[399]).toBeLessThanOrEqual(1400);
  });

  it('is empty for no units', () => {
    expect(smokeDelays(0)).toEqual([]);
    expect(smokeUnits('')).toEqual([]);
  });

  it('splits letters, but words for scripts whose letters join', () => {
    expect(smokeUnits('Ciao.')).toEqual(['C', 'i', 'a', 'o', '.']);
    expect(smokeUnits('你好')).toEqual(['你', '好']);
    expect(smokeUnits('مرحبا بك')).toEqual(['مرحبا ', 'بك']);
    expect(smokeUnits('नमस्ते दुनिया')).toEqual(['नमस्ते ', 'दुनिया']);
  });

  it('lasts longer for longer texts, up to a bound', () => {
    expect(smokeMs(0)).toBe(0);
    expect(smokeMs(5)).toBeLessThan(smokeMs(10));
    expect(smokeMs(500)).toBe(smokeMs(100));
  });
});
