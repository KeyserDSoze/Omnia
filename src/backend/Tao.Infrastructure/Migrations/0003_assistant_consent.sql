-- The assistant briefing is a separate, revocable purpose: it sends minimized daily cards
-- (kind, title, time, counterpart, short preview) to the model configured by the operator.
ALTER TABLE public.consent_grants DROP CONSTRAINT ck_consent_purpose;
ALTER TABLE public.consent_grants ADD CONSTRAINT ck_consent_purpose
    CHECK (purpose IN ('memory.capture', 'assistant.briefing'));
