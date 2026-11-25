-- Daily counters of the assistant's model calls per user: they bound the operator's spend.
-- Only counts and tokens, never prompts or answers.
CREATE TABLE public.assistant_usage (
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    day date NOT NULL,
    calls integer NOT NULL DEFAULT 0,
    tokens bigint NOT NULL DEFAULT 0,
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '35 days'),
    PRIMARY KEY (tenant_id, owner_sub, day)
);
CREATE INDEX ix_assistant_usage_expiry ON public.assistant_usage (expires_at);

ALTER TABLE public.assistant_usage ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.assistant_usage FORCE ROW LEVEL SECURITY;
CREATE POLICY assistant_usage_actor_scope ON public.assistant_usage FOR ALL TO PUBLIC
    USING (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
    WITH CHECK (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''));

-- The SECURITY DEFINER purge runs as the (possibly non-superuser) owner.
DO $$
BEGIN
    EXECUTE format('CREATE POLICY assistant_usage_retention_owner ON public.assistant_usage FOR ALL TO %I USING (expires_at <= now())',
        current_user);
END;
$$;

CREATE OR REPLACE FUNCTION public.tao_purge_expired_work_state(batch_size integer)
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

    DELETE FROM public.assistant_usage AS u
    USING (SELECT tenant_id, owner_sub, day FROM public.assistant_usage WHERE expires_at <= now()
           ORDER BY expires_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE u.tenant_id = c.tenant_id AND u.owner_sub = c.owner_sub AND u.day = c.day;
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
