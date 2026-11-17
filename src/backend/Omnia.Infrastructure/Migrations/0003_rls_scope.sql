-- Defence-in-depth: all memory and consent operations are limited to
-- the verified API tenant+subject established in a transaction-local context.
-- RLS denies access when either context is unset/empty.
-- A superuser/BYPASSRLS role bypasses RLS even with FORCE; the API must
-- connect as a separate restricted role (see PostgresRuntimeGuard).
ALTER TABLE public.memory_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.memory_entries FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_actor_scope ON public.memory_entries
    FOR ALL TO PUBLIC
    USING (
        tenant_id = NULLIF(current_setting('omnia.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('omnia.owner_sub', true), '')
    )
    WITH CHECK (
        tenant_id = NULLIF(current_setting('omnia.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('omnia.owner_sub', true), '')
    );

ALTER TABLE public.consent_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.consent_grants FORCE ROW LEVEL SECURITY;
CREATE POLICY consent_actor_scope ON public.consent_grants
    FOR ALL TO PUBLIC
    USING (
        tenant_id = NULLIF(current_setting('omnia.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('omnia.owner_sub', true), '')
    )
    WITH CHECK (
        tenant_id = NULLIF(current_setting('omnia.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('omnia.owner_sub', true), '')
    );
