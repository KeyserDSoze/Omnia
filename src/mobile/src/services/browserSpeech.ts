import type { EngineRegistry, Listening, SpeechToTextEngine, TextToSpeechEngine } from './aiRouter';

/** Browser engines of the AI router, built on the Web Speech API. */

type RecognitionResult = ArrayLike<{ transcript: string }> & { isFinal: boolean };
type Recognition = {
  lang: string;
  interimResults: boolean;
  continuous: boolean;
  maxAlternatives: number;
  onresult: ((event: { resultIndex: number; results: ArrayLike<RecognitionResult> }) => void) | null;
  onerror: ((event: { error: string }) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
};
export type Voice = { name: string; lang: string; localService: boolean; default: boolean };
type Utterance = {
  voice: Voice | null;
  lang: string;
  rate: number;
  pitch: number;
  onstart: (() => void) | null;
  onend: (() => void) | null;
  onerror: (() => void) | null;
};
type Synthesis = {
  getVoices(): Voice[];
  speak(utterance: Utterance): void;
  cancel(): void;
  addEventListener?(type: 'voiceschanged', listener: () => void): void;
  removeEventListener?(type: 'voiceschanged', listener: () => void): void;
};
// Structural, so that tests can pass fakes.
type SpeechEnvironment = {
  SpeechRecognition?: unknown;
  webkitSpeechRecognition?: unknown;
  speechSynthesis?: unknown;
  SpeechSynthesisUtterance?: unknown;
};

export class SpeechError extends Error {
  constructor(public readonly code: string) { super(code); }
}

/** Message for a recognition error; null when there is nothing to say (the user stopped). */
export function speechErrorMessage(code: string): string | null {
  switch (code) {
    case 'aborted': return null;
    case 'not-allowed':
    case 'service-not-allowed':
      return 'Il browser non mi lascia usare il microfono: consentilo nelle impostazioni del sito.';
    case 'no-speech': return 'Non ho sentito nulla. Riprova quando vuoi.';
    case 'audio-capture': return 'Non trovo un microfono su questo dispositivo.';
    case 'network': return 'Il riconoscimento vocale del browser non è raggiungibile. Puoi scrivere.';
    case 'language-not-supported': return 'Il browser non riconosce l’italiano. Puoi scrivere.';
    default: return 'Dettatura non riuscita. Puoi scrivere.';
  }
}

export function createBrowserSpeechToText(env: SpeechEnvironment = globalThis): SpeechToTextEngine | null {
  const Recognizer = (env.SpeechRecognition ?? env.webkitSpeechRecognition) as (new () => Recognition) | undefined;
  if (typeof Recognizer !== 'function') return null;
  return {
    engine: 'browser',
    // Chrome, Edge and Safari may run recognition on the vendor's servers.
    leavesDevice: true,
    listen({ lang, onInterim }): Listening {
      const recognition = new Recognizer();
      recognition.lang = lang;
      recognition.interimResults = true;
      recognition.continuous = false;
      recognition.maxAlternatives = 1;
      let heard = '';
      let failure: SpeechError | null = null;
      let discarded = false;
      let resolveResult!: (text: string) => void;
      let rejectResult!: (error: SpeechError) => void;
      const result = new Promise<string>((resolve, reject) => { resolveResult = resolve; rejectResult = reject; });
      recognition.onresult = event => {
        let interim = '';
        for (let i = event.resultIndex; i < event.results.length; i++) {
          const item = event.results[i];
          if (item.isFinal) heard += item[0]?.transcript ?? '';
          else interim += item[0]?.transcript ?? '';
        }
        onInterim?.((heard + interim).trim());
      };
      recognition.onerror = event => { failure = new SpeechError(event.error); };
      recognition.onend = () => {
        if (discarded) rejectResult(new SpeechError('aborted'));
        else if (failure && !heard.trim()) rejectResult(failure);
        else resolveResult(heard.trim());
      };
      try { recognition.start(); }
      catch { rejectResult(new SpeechError('unavailable')); }
      return {
        result,
        stop: () => recognition.stop(),
        abort: () => { discarded = true; recognition.abort(); }
      };
    }
  };
}

/** An Italian voice installed on the device first; an online vendor voice only when allowed. */
export function pickVoice(voices: readonly Voice[], allowOnline = false, language = 'it'): Voice | null {
  const italian = voices.filter(v => v.lang.toLowerCase().replace('_', '-').startsWith(language));
  const local = italian.filter(v => v.localService);
  const pool = local.length > 0 ? local : allowOnline ? italian : [];
  return pool.find(v => v.default) ?? pool[0] ?? null;
}

export function createBrowserTextToSpeech(env: SpeechEnvironment = globalThis): TextToSpeechEngine | null {
  const synth = env.speechSynthesis as Synthesis | undefined;
  const Speech = env.SpeechSynthesisUtterance as (new (text: string) => Utterance) | undefined;
  if (!synth || typeof Speech !== 'function') return null;
  let voice: Voice | null = null;
  let loading: Promise<Voice[]> | null = null;
  const voices = () => loading ??= new Promise<Voice[]>(resolve => {
    if (synth.getVoices().length > 0 || !synth.addEventListener) { resolve(synth.getVoices()); return; }
    // Chrome fills the list asynchronously after the first call.
    const done = () => {
      clearTimeout(timer);
      synth.removeEventListener?.('voiceschanged', done);
      resolve(synth.getVoices());
    };
    const timer = setTimeout(done, 2000);
    synth.addEventListener('voiceschanged', done);
  });
  return {
    engine: 'browser',
    async availability() {
      const list = await voices();
      return pickVoice(list) ? 'local' : pickVoice(list, true) ? 'online' : 'none';
    },
    async ready(allowOnline) {
      voice = pickVoice(await voices(), allowOnline);
      return voice !== null;
    },
    speak(text, events) {
      if (!voice) return;
      const utterance = new Speech(text);
      utterance.voice = voice;
      utterance.lang = voice.lang;
      utterance.rate = 1;
      utterance.pitch = 1;
      utterance.onstart = () => events?.onStart?.();
      utterance.onend = () => events?.onEnd?.();
      utterance.onerror = () => events?.onEnd?.();
      synth.speak(utterance);
    },
    cancel() { synth.cancel(); }
  };
}

export const browserEngines: EngineRegistry = {
  browser: { speechToText: () => createBrowserSpeechToText(), textToSpeech: () => createBrowserTextToSpeech() }
};
