import type { EngineRegistry, Listening, SpeechToTextEngine, TextToSpeechEngine } from './aiRouter';
import { t } from '../i18n';

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
      return t('speech.errorMic');
    case 'no-speech': return t('speech.errorSilence');
    case 'audio-capture': return t('speech.errorNoMic');
    case 'network': return t('speech.errorNetwork');
    case 'language-not-supported': return t('speech.errorLanguage');
    default: return t('speech.errorOther');
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

/**
 * A voice for the BCP 47 tag: same primary language, the tag's region first. A voice installed
 * on the device wins; an online vendor voice only when allowed.
 */
export function pickVoice(voices: readonly Voice[], allowOnline: boolean, tag: string): Voice | null {
  const parts = (lang: string) => lang.toLowerCase().replace(/_/g, '-').split('-');
  const [language, region] = parts(tag);
  const same = voices.filter(v => parts(v.lang)[0] === language);
  const best = (pool: Voice[]) => {
    const regional = pool.filter(v => region !== undefined && parts(v.lang)[1] === region);
    const list = regional.length > 0 ? regional : pool;
    return list.find(v => v.default) ?? list[0] ?? null;
  };
  const local = same.filter(v => v.localService);
  return local.length > 0 ? best(local) : allowOnline ? best(same) : null;
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
    async availability(lang) {
      const list = await voices();
      return pickVoice(list, false, lang) ? 'local' : pickVoice(list, true, lang) ? 'online' : 'none';
    },
    async ready(allowOnline, lang) {
      voice = pickVoice(await voices(), allowOnline, lang);
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
