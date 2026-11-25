import { describe, expect, it } from 'vitest';
import {
  createAiRouter, defaultRoutes, routesFrom, type SpeechToTextEngine, type TextToSpeechEngine
} from '../../src/mobile/src/services/aiRouter';

const stt: SpeechToTextEngine = {
  engine: 'browser', leavesDevice: true,
  listen: () => ({ result: Promise.resolve(''), stop() { }, abort() { } })
};
const tts: TextToSpeechEngine = {
  engine: 'browser', availability: async () => 'local', ready: async () => true, speak() { }, cancel() { }
};
const registry = { browser: { speechToText: () => stt, textToSpeech: () => tts } };

describe('AI router', () => {
  it('runs speech in the browser by default and never assumes a briefing model', () => {
    expect(defaultRoutes).toEqual({ briefing: 'off', speechToText: 'browser', textToSpeech: 'browser' });
    expect(routesFrom(undefined)).toEqual(defaultRoutes);
    expect(routesFrom({ briefing: 'server', speechToText: 'off', textToSpeech: 'browser' }))
      .toEqual({ briefing: 'server', speechToText: 'off', textToSpeech: 'browser' });
  });

  it('fails closed on unknown engines sent by the server', () => {
    expect(routesFrom({ briefing: 'cloud', speechToText: 'whisper', textToSpeech: 7 }))
      .toEqual({ briefing: 'off', speechToText: 'off', textToSpeech: 'off' });
  });

  it('uses the engine registered for each route and nothing when a route is off', () => {
    const router = createAiRouter(defaultRoutes, registry);
    expect(router.speechToText).toBe(stt);
    expect(router.textToSpeech).toBe(tts);
    const noDictation = createAiRouter({ ...defaultRoutes, speechToText: 'off' }, registry);
    expect(noDictation.speechToText).toBeNull();
    expect(noDictation.textToSpeech).toBe(tts);
  });

  it('hides a feature the browser lacks, or a server engine this client does not have yet', () => {
    expect(createAiRouter(defaultRoutes, { browser: { speechToText: () => null } }).speechToText).toBeNull();
    expect(createAiRouter({ ...defaultRoutes, speechToText: 'server' }, registry).speechToText).toBeNull();
  });
});
