-- Reminders and Web Push notifications (ADR 0113). Reminder texts and push endpoints are sealed by the
-- application; every table that holds them is actor-scoped with FORCE RLS.
ALTER TABLE public.consent_grants DROP CONSTRAINT ck_consent_purpose;
ALTER TABLE public.consent_grants ADD CONSTRAINT ck_consent_purpose
    CHECK (purpose IN ('memory.capture', 'assistant.briefing', 'speech.browser', 'mcp.access', 'notifications.push'));

CREATE TABLE public.reminders (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    sealed_text bytea NOT NULL,
    repeat varchar(16) NOT NULL CHECK (repeat IN ('once', 'daily', 'weekdays')),
    at_time time NOT NULL,
    on_date date,
    time_zone varchar(64) NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    next_run_at timestamptz,
    last_sent_at timestamptz,
    claimed_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_reminders_actor ON public.reminders (tenant_id, owner_sub, created_at);
CREATE INDEX ix_reminders_due ON public.reminders (next_run_at) WHERE enabled AND next_run_at IS NOT NULL;

CREATE TABLE public.push_subscriptions (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    endpoint_hash bytea NOT NULL,
    sealed_subscription bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_success_at timestamptz,
    failures integer NOT NULL DEFAULT 0,
    UNIQUE (tenant_id, owner_sub, endpoint_hash)
);

-- The server's own VAPID key, sealed; not personal data and not actor-scoped.
CREATE TABLE public.push_settings (
    id smallint PRIMARY KEY CHECK (id = 1),
    sealed_vapid_key bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.reminders ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.reminders FORCE ROW LEVEL SECURITY;
CREATE POLICY reminders_actor_scope ON public.reminders FOR ALL TO PUBLIC
    USING (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
    WITH CHECK (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''));

ALTER TABLE public.push_subscriptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.push_subscriptions FORCE ROW LEVEL SECURITY;
CREATE POLICY push_subscriptions_actor_scope ON public.push_subscriptions FOR ALL TO PUBLIC
    USING (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''))
    WITH CHECK (tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
           AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), ''));

-- The SECURITY DEFINER functions run as the (possibly non-superuser) owner, which only ever sees these rows.
DO $$
BEGIN
    EXECUTE format('CREATE POLICY reminders_claim_owner ON public.reminders FOR ALL TO %I USING (enabled AND next_run_at IS NOT NULL AND next_run_at <= now())',
        current_user);
    EXECUTE format('CREATE POLICY reminders_retention_owner ON public.reminders FOR ALL TO %I USING (repeat = ''once'' AND next_run_at IS NULL AND created_at < now() - interval ''30 days'')',
        current_user);
    EXECUTE format('CREATE POLICY push_subscriptions_retention_owner ON public.push_subscriptions FOR ALL TO %I USING (failures >= 5 OR COALESCE(last_success_at, created_at) < now() - interval ''120 days'')',
        current_user);
END;
$$;

-- Marks the reminders that are due as claimed for a while and returns who they belong to: identifiers only.
CREATE FUNCTION public.tao_claim_due_reminders(batch_size integer, lease_seconds integer)
RETURNS TABLE (o_tenant varchar, o_owner varchar, o_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 200
       OR lease_seconds IS NULL OR lease_seconds < 30 OR lease_seconds > 3600 THEN
        RAISE EXCEPTION 'Invalid claim parameters' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
    UPDATE public.reminders AS r SET claimed_until = now() + make_interval(secs => lease_seconds)
    FROM (SELECT x.id FROM public.reminders AS x
          WHERE x.enabled AND x.next_run_at IS NOT NULL AND x.next_run_at <= now()
            AND (x.claimed_until IS NULL OR x.claimed_until < now())
          ORDER BY x.next_run_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE r.id = c.id
    RETURNING r.tenant_id, r.owner_sub, r.id;
END;
$$;

-- Finished one-off reminders and subscriptions that no longer work.
CREATE FUNCTION public.tao_purge_stale_notifications(batch_size integer)
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

    DELETE FROM public.reminders AS r
    USING (SELECT id FROM public.reminders
           WHERE repeat = 'once' AND next_run_at IS NULL AND created_at < now() - interval '30 days'
           ORDER BY created_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE r.id = c.id;
    GET DIAGNOSTICS step = ROW_COUNT;
    affected := affected + step;

    DELETE FROM public.push_subscriptions AS s
    USING (SELECT id FROM public.push_subscriptions
           WHERE failures >= 5 OR COALESCE(last_success_at, created_at) < now() - interval '120 days'
           ORDER BY created_at LIMIT batch_size FOR UPDATE SKIP LOCKED) AS c
    WHERE s.id = c.id;
    GET DIAGNOSTICS step = ROW_COUNT;
    RETURN affected + step;
END;
$$;

REVOKE ALL ON FUNCTION public.tao_claim_due_reminders(integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.tao_purge_stale_notifications(integer) FROM PUBLIC;
