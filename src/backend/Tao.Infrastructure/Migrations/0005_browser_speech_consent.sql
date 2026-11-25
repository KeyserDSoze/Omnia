-- The browser's speech services are a separate, revocable purpose: dictation may send the user's
-- audio, and an online browser voice the text it reads, to the browser vendor. Tao itself never
-- receives or stores audio.
ALTER TABLE public.consent_grants DROP CONSTRAINT ck_consent_purpose;
ALTER TABLE public.consent_grants ADD CONSTRAINT ck_consent_purpose
    CHECK (purpose IN ('memory.capture', 'assistant.briefing', 'speech.browser'));
