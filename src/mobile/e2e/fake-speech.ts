import type { Page } from '@playwright/test';

/**
 * A headless browser can neither hear nor speak: deterministic stand-ins for the Web Speech API.
 * Dictation always hears "cosa devo fare oggi"; Tao's voice is local and records what it reads.
 */
export async function fakeBrowserSpeech(page: Page) {
  await page.addInitScript(() => {
    const spoken: string[] = [];
    Object.defineProperty(window, '__spoken', { value: spoken, configurable: true });
    class Recognition {
      lang = '';
      interimResults = false;
      continuous = false;
      maxAlternatives = 1;
      onresult: ((event: unknown) => void) | null = null;
      onerror: ((event: unknown) => void) | null = null;
      onend: (() => void) | null = null;
      start() {
        setTimeout(() => {
          const result = Object.assign([{ transcript: 'cosa devo fare oggi' }], { isFinal: true });
          this.onresult?.({ resultIndex: 0, results: [result] });
          this.onend?.();
        }, 300);
      }
      stop() { this.onend?.(); }
      abort() { this.onend?.(); }
    }
    class Utterance {
      voice = null;
      lang = '';
      rate = 1;
      pitch = 1;
      onstart: (() => void) | null = null;
      onend: (() => void) | null = null;
      onerror: (() => void) | null = null;
      constructor(public text: string) { }
    }
    const local = { name: 'Voce locale', lang: 'it-IT', localService: true, default: true };
    Object.defineProperty(window, 'SpeechRecognition', { value: Recognition, configurable: true });
    Object.defineProperty(window, 'webkitSpeechRecognition', { value: Recognition, configurable: true });
    Object.defineProperty(window, 'SpeechSynthesisUtterance', { value: Utterance, configurable: true });
    Object.defineProperty(window, 'speechSynthesis', {
      configurable: true,
      value: {
        getVoices: () => [local],
        speak: (u: Utterance) => { spoken.push(u.text); u.onstart?.(); setTimeout(() => u.onend?.(), 50); },
        cancel: () => undefined
      }
    });
  });
}

export const spokenLines = (page: Page) =>
  page.evaluate(() => (window as unknown as { __spoken: string[] }).__spoken.length);
