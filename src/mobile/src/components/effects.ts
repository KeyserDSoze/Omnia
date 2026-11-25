/** Timing and choices of the visual effects; pure, so they can be tested without a renderer. */

export type Whim = 'reverse' | 'flip' | 'tumble' | 'burst';

const whims: readonly { kind: Whim; weight: number }[] = [
  { kind: 'reverse', weight: 0.35 }, { kind: 'flip', weight: 0.30 },
  { kind: 'tumble', weight: 0.15 }, { kind: 'burst', weight: 0.20 }
];

/** What the Tao symbol does next on a whim, and how long it waits before doing it. */
export function nextWhim(random: () => number = Math.random, previous?: Whim): { kind: Whim; waitMs: number } {
  const roll = random();
  let sum = 0;
  let index = whims.length - 1;
  for (let i = 0; i < whims.length; i++) {
    sum += whims[i].weight;
    if (roll < sum) { index = i; break; }
  }
  if (whims[index].kind === previous) index = (index + 1) % whims.length;
  return { kind: whims[index].kind, waitMs: 4000 + Math.floor(random() * 7000) };
}

/** Start delay (ms) of each letter or word of a greeting that emerges from smoke. */
export function smokeDelays(units: number): number[] {
  return Array.from({ length: units }, (_, i) => Math.min(i * 70, 1400));
}

const SMOKE_MS = 1500;
/** How long a greeting takes to appear completely. */
export function smokeMs(units: number): number {
  return units === 0 ? 0 : smokeDelays(units)[units - 1] + SMOKE_MS;
}

/** Scripts whose letters join: they emerge word by word, never letter by letter. */
export const joinedScript = /[\u0590-\u08FF\u0900-\u0DFF\u0E00-\u0E7F]/u;

/** Letters (or words, for joined scripts) of a text, with spaces attached to the previous unit. */
export function smokeUnits(text: string): string[] {
  if (joinedScript.test(text)) return text.split(/(?<=\s)/u).filter(Boolean);
  const segmenter = typeof Intl.Segmenter === 'function' ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
  return segmenter ? Array.from(segmenter.segment(text), s => s.segment) : Array.from(text);
}

/** Stable pseudo-random number in [0, 1) for a unit, so a greeting always drifts the same way. */
export function wisp(index: number, salt = 0): number {
  const x = Math.sin((index + 1) * 12.9898 + salt * 78.233) * 43758.5453;
  return x - Math.floor(x);
}
