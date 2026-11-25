# ADR 0103 — Internationalization: 21 languages

- Date: 2026-11-26
- Status: accepted (implemented on `main`)

## Context

Tao must speak at least the 20 most spoken languages. The text is in the chat, the settings, the
setup, the deck, errors, speech and the AI plan. The app is a static export that must also work on
GitHub Pages, so a heavy runtime or a server catalog is not an option.

## Decision

- **21 locales**: Italian (the source), English, Spanish, French, German, Portuguese, Russian,
  Polish, Dutch, Turkish, Indonesian, Vietnamese, Chinese, Japanese, Korean, Thai, Hindi, Bengali,
  Arabic, Urdu and Persian. Arabic, Urdu and Persian are right-to-left.
- **No dependency.** A small store in `src/mobile/src/i18n`:
  - `useT()` / `useLocale()` for components (`useSyncExternalStore`, server snapshot `en`) and `t()`
    for services; `{param}` interpolation with `Intl.NumberFormat`; plurals with `Intl.PluralRules`.
  - Catalogs in `messages/<code>.ts`, typed `Messages` (Italian is the source): a missing key is a
    compile error, and a unit test checks keys and placeholders against Italian.
- **Preference** is `localStorage` `tao.locale`, then `navigator.languages`, then English. It is not
  sent to the server and not stored in the database.
- **RTL**: `dir` and `lang` on the root view and on `document.documentElement`.
- **Intents** per language through `intent.*` keys (words separated by `|`, a trailing `*` for a
  prefix, with an English fallback); matching is Unicode-aware, so the chat understands each language.
- **Sign-in** passes `ui_locales` to the identity provider.
- **Backend language for the AI plan.**
  - `POST /api/v1/today/briefing` accepts `language`, a code from an allow-list
    (`AssistantLanguages`). Free text is rejected with `400 invalid_language`, so nothing the user
    types can reach the system prompt; the prompt receives only the English name from the list.
  - No code means Italian. Dates are formatted in the language's culture but always with the
    Gregorian calendar, so Thai or Arabic cultures do not produce other calendars.
  - With no open cards the server returns an empty summary and the client says it in the user's
    language; the model is not called.
  - The system prompt is now English and declares the card content as UNTRUSTED.

## Adding a language

1. Add the entry in `locales.ts`, copy `messages/it.ts` and translate it (keys must match).
2. Add the code and English name in `AssistantLanguages.cs`.
3. Run typecheck, `npm test` and the Domain tests (the count is asserted).

## Consequences

- Translations were produced without native review: they should be reviewed before a public release.
- The bundle grows by the catalogs; they are plain objects and compress well.
- The language is per browser, not per account: a new device starts from its own browser language.
