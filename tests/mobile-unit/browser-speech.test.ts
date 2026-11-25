import { describe, expect, it, vi } from 'vitest';
import {
  createBrowserSpeechToText, createBrowserTextToSpeech, pickVoice, speechErrorMessage
} from '../../src/mobile/src/services/browserSpeech';

type Handler<T> = ((event: T) => void) | null;

class FakeRecognition {
  static last: FakeRecognition;
  lang = '';
  interimResults = false;
  continuous = true;
  maxAlternatives = 0;
  onresult: Handler<unknown> = null;
  onerror: Handler<{ error: string }> = null;
  onend: (() => void) | null = null;
  started = false;
  constructor() { FakeRecognition.last = this; }
  start() { this.started = true; }
  stop() { this.onend?.(); }
  abort() { this.onend?.(); }
  emit(resultIndex: number, results: { text: string; final: boolean }[]) {
    this.onresult?.({
      resultIndex,
      results: results.map(r => Object.assign([{ transcript: r.text }], { isFinal: r.final }))
    });
  }
}

describe('browser dictation', () => {
  it('is unavailable without the Web Speech API', () => {
    expect(createBrowserSpeechToText({})).toBeNull();
  });

  it('streams what it hears and resolves with the final Italian transcript', async () => {
    const engine = createBrowserSpeechToText({ webkitSpeechRecognition: FakeRecognition })!;
    expect(engine.leavesDevice).toBe(true);
    const interim: string[] = [];
    const session = engine.listen({ lang: 'it-IT', onInterim: text => interim.push(text) });
    const recognition = FakeRecognition.last;
    expect([recognition.lang, recognition.interimResults, recognition.continuous, recognition.started])
      .toEqual(['it-IT', true, false, true]);
    recognition.emit(0, [{ text: 'cosa devo', final: false }]);
    recognition.emit(0, [{ text: 'cosa devo fare oggi', final: true }]);
    recognition.onend?.();
    await expect(session.result).resolves.toBe('cosa devo fare oggi');
    expect(interim).toEqual(['cosa devo', 'cosa devo fare oggi']);
  });

  it('reports errors by code, and an aborted session discards what it heard', async () => {
    const engine = createBrowserSpeechToText({ SpeechRecognition: FakeRecognition })!;
    const denied = engine.listen({ lang: 'it-IT' });
    FakeRecognition.last.onerror?.({ error: 'not-allowed' });
    FakeRecognition.last.onend?.();
    await expect(denied.result).rejects.toMatchObject({ code: 'not-allowed' });

    const discarded = engine.listen({ lang: 'it-IT' });
    FakeRecognition.last.emit(0, [{ text: 'ciao', final: true }]);
    discarded.abort();
    await expect(discarded.result).rejects.toMatchObject({ code: 'aborted' });

    expect(speechErrorMessage('aborted')).toBeNull();
    expect(speechErrorMessage('not-allowed')).toContain('microfono');
    expect(speechErrorMessage('something-new')).toContain('scrivere');
  });
});

describe('Tao voice', () => {
  const voices = [
    { name: 'Google italiano', lang: 'it-IT', localService: false, default: false },
    { name: 'Alice', lang: 'it-IT', localService: true, default: false },
    { name: 'Samantha', lang: 'en-US', localService: true, default: true }
  ];

  it('prefers Italian voices installed on the device; an online voice only when allowed', () => {
    expect(pickVoice(voices)?.name).toBe('Alice');
    expect(pickVoice(voices, true)?.name).toBe('Alice');
    const online = voices.filter(v => v.name !== 'Alice');
    expect(pickVoice(online)).toBeNull();
    expect(pickVoice(online, true)?.name).toBe('Google italiano');
    expect(pickVoice([{ name: 'Elsa', lang: 'it_IT', localService: true, default: false }])?.name).toBe('Elsa');
  });

  it('tells whether the voice is local, online only, or missing', async () => {
    const engine = (list: typeof voices) => createBrowserTextToSpeech({
      speechSynthesis: { getVoices: () => list, speak: vi.fn(), cancel: vi.fn() }, SpeechSynthesisUtterance: class { }
    })!;
    expect(await engine(voices).availability()).toBe('local');
    expect(await engine(voices.filter(v => v.name !== 'Alice')).availability()).toBe('online');
    expect(await engine(voices.filter(v => v.lang === 'en-US')).availability()).toBe('none');
    expect(await engine(voices.filter(v => v.name !== 'Alice')).ready(false)).toBe(false);
    expect(await engine(voices.filter(v => v.name !== 'Alice')).ready(true)).toBe(true);
  });

  it('speaks with the local voice and reports start and end', async () => {
    const spoken: { text: string; lang: string; voice: { name: string } | null }[] = [];
    class Utterance {
      voice: { name: string } | null = null;
      lang = '';
      rate = 0;
      pitch = 0;
      onstart: (() => void) | null = null;
      onend: (() => void) | null = null;
      onerror: (() => void) | null = null;
      constructor(public text: string) { }
    }
    const synth = {
      getVoices: () => voices,
      speak: (u: Utterance) => { spoken.push(u); u.onstart?.(); u.onend?.(); },
      cancel: vi.fn()
    };
    const engine = createBrowserTextToSpeech({ speechSynthesis: synth, SpeechSynthesisUtterance: Utterance })!;
    expect(await engine.ready(false)).toBe(true);
    const events: string[] = [];
    engine.speak('Buongiorno!', { onStart: () => events.push('start'), onEnd: () => events.push('end') });
    expect(spoken[0]).toMatchObject({ text: 'Buongiorno!', lang: 'it-IT' });
    expect(spoken[0].voice?.name).toBe('Alice');
    expect(events).toEqual(['start', 'end']);
    engine.cancel();
    expect(synth.cancel).toHaveBeenCalled();
  });

  it('waits for the voice list that Chrome fills asynchronously', async () => {
    let list: typeof voices = [];
    let changed: (() => void) | null = null;
    const synth = {
      getVoices: () => list, speak: vi.fn(), cancel: vi.fn(),
      addEventListener: (_: 'voiceschanged', listener: () => void) => { changed = listener; },
      removeEventListener: vi.fn()
    };
    const engine = createBrowserTextToSpeech({ speechSynthesis: synth, SpeechSynthesisUtterance: class { } })!;
    const ready = engine.ready(false);
    list = voices;
    changed!();
    expect(await ready).toBe(true);
    expect(createBrowserTextToSpeech({})).toBeNull();
  });
});
