-- Letting another AI (an MCP client) read the user's day through Tao's MCP server is a separate,
-- revocable purpose (ADR 0112). No new table: the grant lives in the consent ledger.
ALTER TABLE public.consent_grants DROP CONSTRAINT ck_consent_purpose;
ALTER TABLE public.consent_grants ADD CONSTRAINT ck_consent_purpose
    CHECK (purpose IN ('memory.capture', 'assistant.briefing', 'speech.browser', 'mcp.access'));
