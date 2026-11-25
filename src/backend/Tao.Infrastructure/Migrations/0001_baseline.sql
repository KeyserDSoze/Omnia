-- Tao baseline schema (fresh start: no legacy plaintext columns).
-- All personal content is sealed by the application (AES-256-GCM envelope);
-- RLS scopes every row to the tenant+subject taken from the signed OIDC token.
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE public.memory_entries (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    source varchar(120) NOT NULL,
    external_id varchar(255) NOT NULL,
    content_type varchar(120) NOT NULL,
    -- Raw content, normalized JSON, summary and tags (AEAD-bound to tenant/owner/id).
    sealed_payload bytea NOT NULL,
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    -- The GDPR does not prescribe 30 days: this is a provisional product policy.
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '30 days'),
    embedding vector(768),
    embedding_model text,
    CONSTRAINT uq_memory_external UNIQUE (tenant_id, owner_sub, source, external_id)
);
CREATE INDEX ix_memory_recent ON public.memory_entries (tenant_id, owner_sub, occurred_at DESC);
CREATE INDEX ix_memory_expiry ON public.memory_entries (expires_at, id);
CREATE INDEX ix_memory_vector ON public.memory_entries USING hnsw (embedding vector_cosine_ops);

-- First-party, source-scoped authorization records. A user acknowledgement is not
-- a legal basis for processing conversations or recordings involving other people.
CREATE TABLE public.consent_grants (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    source varchar(120) NOT NULL,
    purpose varchar(64) NOT NULL,
    policy_version varchar(64) NOT NULL,
    granted_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    CONSTRAINT ck_consent_purpose CHECK (purpose = 'memory.capture'),
    CONSTRAINT ck_consent_validity CHECK (expires_at > granted_at)
);
-- Exactly one unrevoked grant per scope; a new grant supersedes the prior one.
CREATE UNIQUE INDEX uq_consent_active_scope ON public.consent_grants
    (tenant_id, owner_sub, source, purpose) WHERE revoked_at IS NULL;
CREATE INDEX ix_consent_history ON public.consent_grants (tenant_id, owner_sub, granted_at DESC);

-- Missing/empty actor context denies access. SUPERUSER/BYPASSRLS ignore RLS even
-- with FORCE: the API must use a restricted role (see PostgresRuntimeGuard).
ALTER TABLE public.memory_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.memory_entries FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_actor_scope ON public.memory_entries
    FOR ALL TO PUBLIC
    USING (
        tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), '')
    )
    WITH CHECK (
        tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), '')
    );

ALTER TABLE public.consent_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.consent_grants FORCE ROW LEVEL SECURITY;
CREATE POLICY consent_actor_scope ON public.consent_grants
    FOR ALL TO PUBLIC
    USING (
        tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), '')
    )
    WITH CHECK (
        tenant_id = NULLIF(current_setting('tao.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('tao.owner_sub', true), '')
    );

-- Fixed-body SECURITY DEFINER purge: EXECUTE is granted only to the dedicated
-- retention role by the migrator, never to the API runtime role.
-- FORCE RLS also binds a non-superuser owner, so the owner may only reach expired rows.
DO $$
BEGIN
    EXECUTE format(
        'CREATE POLICY memory_retention_owner ON public.memory_entries FOR ALL TO %I USING (expires_at <= now())',
        current_user);
END;
$$;

CREATE FUNCTION public.tao_purge_expired_memories(batch_size integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    affected integer;
BEGIN
    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 500 THEN
        RAISE EXCEPTION 'Invalid retention batch size' USING ERRCODE = '22023';
    END IF;

    WITH candidates AS (
        SELECT id
        FROM public.memory_entries
        WHERE expires_at <= now()
        ORDER BY expires_at, id
        LIMIT batch_size
        FOR UPDATE SKIP LOCKED
    )
    DELETE FROM public.memory_entries AS m
    USING candidates AS c
    WHERE m.id = c.id;

    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected;
END;
$$;
REVOKE ALL ON FUNCTION public.tao_purge_expired_memories(integer) FROM PUBLIC;
