-- Connected work accounts (Microsoft 365, Google Workspace) and the daily triage state.
-- OAuth tokens, PKCE verifiers and account identities are sealed by the application
-- (AES-GCM envelope bound to tenant, owner and record id). Every table is actor-scoped by RLS.
CREATE TABLE public.connector_authorizations (
    state_hash bytea PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    provider varchar(32) NOT NULL,
    sealed_verifier bytea NOT NULL,
    expires_at timestamptz NOT NULL,
    CONSTRAINT ck_connector_authorization_state CHECK (octet_length(state_hash) = 32)
);
CREATE INDEX ix_connector_authorizations_expiry ON public.connector_authorizations (expires_at);

CREATE TABLE public.connector_accounts (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    provider varchar(32) NOT NULL,
    external_account_id varchar(255) NOT NULL,
    sealed_credentials bytea NOT NULL,
    connected_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_connector_account UNIQUE (tenant_id, owner_sub, provider, external_account_id)
);

-- Only the item key and the user's decision are stored, never the provider content.
CREATE TABLE public.triage_decisions (
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    item_key varchar(512) NOT NULL,
    decision varchar(16) NOT NULL,
    decided_at timestamptz NOT NULL,
    snoozed_until timestamptz,
    applied_to_source boolean NOT NULL DEFAULT false,
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '30 days'),
    PRIMARY KEY (tenant_id, owner_sub, item_key),
    CONSTRAINT ck_triage_decision CHECK (decision IN ('done', 'not_done', 'later', 'dismissed'))
);
CREATE INDEX ix_triage_expiry ON public.triage_decisions (expires_at);

-- Audit trail of every write Tao performs on a provider on the user's behalf.
CREATE TABLE public.connector_actions (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    account_id uuid NOT NULL,
    action varchar(64) NOT NULL,
    outcome varchar(16) NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '180 days'),
    CONSTRAINT ck_connector_action_outcome CHECK (outcome IN ('succeeded', 'failed'))
);
CREATE INDEX ix_connector_actions_actor ON public.connector_actions (tenant_id, owner_sub, requested_at DESC);
CREATE INDEX ix_connector_actions_expiry ON public.connector_actions (expires_at);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['connector_authorizations', 'connector_accounts',
                             'triage_decisions', 'connector_actions']
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$
            CREATE POLICY %I ON public.%I FOR ALL TO PUBLIC
            USING (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
                   AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
            WITH CHECK (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
                   AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
            $p$, t || '_actor_scope', t);
    END LOOP;
    -- The SECURITY DEFINER purge below runs as the (possibly non-superuser) owner.
    FOREACH t IN ARRAY ARRAY['connector_authorizations', 'triage_decisions', 'connector_actions']
    LOOP
        EXECUTE format('CREATE POLICY %I ON public.%I FOR ALL TO %I USING (expires_at <= now())',
            t || '_retention_owner', t, current_user);
    END LOOP;
END;
$$;

CREATE FUNCTION public.tao_purge_expired_work_state(batch_size integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    affected integer := 0;
    step integer;
BEGIN
    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 500 THEN
        RAISE EXCEPTION 'Invalid retention batch size' USING ERRCODE = '22023';
    END IF;

    DELETE FROM public.connector_authorizations AS a
    USING (SELECT state_hash FROM public.connector_authorizations WHERE expires_at <= now()
           ORDER BY expires_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE a.state_hash = c.state_hash;
    GET DIAGNOSTICS step = ROW_COUNT;
    affected := affected + step;

    DELETE FROM public.triage_decisions AS d
    USING (SELECT tenant_id, owner_sub, item_key FROM public.triage_decisions WHERE expires_at <= now()
           ORDER BY expires_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE d.tenant_id = c.tenant_id AND d.owner_sub = c.owner_sub AND d.item_key = c.item_key;
    GET DIAGNOSTICS step = ROW_COUNT;
    affected := affected + step;

    DELETE FROM public.connector_actions AS a
    USING (SELECT id FROM public.connector_actions WHERE expires_at <= now()
           ORDER BY expires_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE a.id = c.id;
    GET DIAGNOSTICS step = ROW_COUNT;
    RETURN affected + step;
END;
$$;
REVOKE ALL ON FUNCTION public.tao_purge_expired_work_state(integer) FROM PUBLIC;
