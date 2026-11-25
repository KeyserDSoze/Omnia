# ADR 0102 — Immersive chat, AI router and browser speech

- Date: 2026-11-26
- Status: accepted (implemented on `main`)

## Context

The home should feel like talking to the Tao symbol, not like using a dashboard: a tiny header, a
living symbol, a greeting, and sign-in with Microsoft or Google inside the conversation. Dictation
and Tao's voice should work today in the browser (Web Speech API) and later on the server, without
touching the chat when that happens. The look should be memorable, but must stay accessible and must
not make the tests flaky.

## Decision

- **Chat as the whole home.**
  - Before sign-in the screen shows only the symbol, the greeting and the Microsoft and Google
    buttons. After sign-in the setup starts from the chat; the settings are in a very small header.
  - Intents stay deterministic keyword matches (`chatScript.ts`), no AI.
- **AI router.**
  - `aiRouter.ts` maps each AI task (`briefing`, `speechToText`, `textToSpeech`) to an engine:
    `off`, `browser` or `server`.
  - The server owns the routing table (`Ai:SpeechToText`, `Ai:TextToSpeech`, `AiRouting` in
    `Tao.Application`), exposed in `GET /api/v1/assistant`. The client treats unknown engines as `off`.
    A server speech engine does not exist yet: the configuration accepts only `browser` or `off`
    and refuses to start otherwise, so it never falls back silently.
  - The client keeps a registry of engines. Adding a server engine means registering it and
    switching the route; the chat does not change.
- **Browser speech needs its own consent.**
  - Scope `speech.browser` (`ConsentGrant.BrowserSpeech`), asked before the first dictation or
    online voice, because the browser vendor may process audio or text outside the device.
  - A voice installed on the device is preferred; an online voice is used only with the consent.
    When no voice exists for the language the user is told so.
  - nginx sends `Permissions-Policy: microphone=(self)`; camera and geolocation stay denied.
- **Effects are decoration, not behaviour.**
  - Web only. React Native Web cannot run keyframes from inline styles, so `Emerge.tsx` injects one
    global stylesheet and components tag elements with `data-tao`.
  - The greeting comes out of smoke (per letter, per word for joined scripts), the buttons dissolve
    in and the sign-in buttons pulse. The symbol turns, reverses, flips or tumbles at random.
  - The pulse animates `box-shadow` only, never `transform`, so Playwright's stability check on
    clicks still works. Under reduced motion nothing is tagged and nothing moves.
  - Native clients fall back to a plain fade.

## Consequences

- Dictation works today without a server speech provider, with a clear and revocable consent.
- A future server engine only needs a registry entry and a configuration value.
- The effects have unit tests for their pure parts (`effects.ts`, `avatar-whims.test.ts`) and e2e
  tests that check the tags, the animations and the reduced-motion case.
- Voice quality and availability depend on the browser and the device.
