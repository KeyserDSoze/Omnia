/**
 * AI router: which engine runs each AI task. Today dictation and Tao's voice run in the browser
 * (Web Speech API); a server engine can be added later by registering it here and switching the
 * route on the server (Ai:SpeechToText / Ai:TextToSpeech), without touching the chat.
 */
export type AiEngine = 'off' | 'browser' | 'server';
export type AiRoutesDto = { briefing: AiEngine; speechToText: AiEngine; textToSpeech: AiEngine };

/** Before sign-in, in the demo and with an older API: speech in the browser, no briefing model. */
export const defaultRoutes: AiRoutesDto = { briefing: 'off', speechToText: 'browser', textToSpeech: 'browser' };

export interface Listening {
  /** Final transcript, '' when nothing was understood. */
  readonly result: Promise<string>;
  /** Stops listening and keeps what was heard so far. */
  stop(): void;
  /** Stops listening and discards everything. */
  abort(): void;
}

export interface SpeechToTextEngine {
  readonly engine: AiEngine;
  /** Audio may be processed outside the device (e.g. by the browser vendor): needs a dedicated consent. */
  readonly leavesDevice: boolean;
  listen(options: { lang: string; onInterim?: (text: string) => void }): Listening;
}

export interface TextToSpeechEngine {
  readonly engine: AiEngine;
  /** 'local': a voice installed on the device; 'online': only a vendor voice (the text leaves the device). */
  availability(): Promise<'local' | 'online' | 'none'>;
  /** Chooses the voice (an online one only when allowed); false when none is suitable. */
  ready(allowOnline: boolean): Promise<boolean>;
  speak(text: string, events?: { onStart?: () => void; onEnd?: () => void }): void;
  cancel(): void;
}

export type EngineRegistry = Partial<Record<Exclude<AiEngine, 'off'>, {
  speechToText?: () => SpeechToTextEngine | null;
  textToSpeech?: () => TextToSpeechEngine | null;
}>>;

export type AiRouter = {
  routes: AiRoutesDto;
  speechToText: SpeechToTextEngine | null;
  textToSpeech: TextToSpeechEngine | null;
};

const engines: readonly AiEngine[] = ['off', 'browser', 'server'];
const engineOf = (value: unknown): AiEngine =>
  engines.includes(value as AiEngine) ? value as AiEngine : 'off';

/** Validates the server's routing table; unknown engines are treated as off. */
export function routesFrom(value: unknown): AiRoutesDto {
  if (typeof value !== 'object' || value === null) return defaultRoutes;
  const routes = value as Record<string, unknown>;
  return {
    briefing: engineOf(routes.briefing),
    speechToText: engineOf(routes.speechToText),
    textToSpeech: engineOf(routes.textToSpeech)
  };
}

export function createAiRouter(routes: AiRoutesDto, registry: EngineRegistry): AiRouter {
  const entry = (engine: AiEngine) => engine === 'off' ? undefined : registry[engine];
  return {
    routes,
    speechToText: entry(routes.speechToText)?.speechToText?.() ?? null,
    textToSpeech: entry(routes.textToSpeech)?.textToSpeech?.() ?? null
  };
}

export const noAiRouter: AiRouter = { routes: defaultRoutes, speechToText: null, textToSpeech: null };
