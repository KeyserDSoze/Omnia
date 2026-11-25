-- Settings composed by the guided setup: declared accounts, per-account delegations for the agent,
-- work tools and hours. The document is sealed by the application; one row per actor.
CREATE TABLE public.user_settings (
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    sealed_settings bytea NOT NULL,
    onboarding_completed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, owner_sub)
);

ALTER TABLE public.user_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.user_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY user_settings_actor_scope ON public.user_settings FOR ALL TO PUBLIC
    USING (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
    WITH CHECK (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''));
