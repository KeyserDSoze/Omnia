-- #141: bounded, read-only outbox metadata dispatch surface.
-- Only a separately provisioned non-owner login can receive EXECUTE.
-- No runtime/retention/backfill/audit role can call this function.
-- This is not an ACK, delivery proof, transactionally ordered log,
-- immutable/offsite journal, or anti-rollback checkpoint.
DO $owner_policy$
DECLARE schema_owner name;
BEGIN
    SELECT pg_get_userbyid(relowner) INTO schema_owner
    FROM pg_class WHERE oid = 'public.erasure_outbox'::regclass;
    IF schema_owner IS NULL OR schema_owner <> current_user THEN
        RAISE EXCEPTION 'Outbox dispatch migration requires the schema owner'
            USING ERRCODE = '42501';
    END IF;
    -- FORCE RLS also applies to a non-superuser owner. The fixed
    -- SECURITY DEFINER routine uses this single owner-specific policy;
    -- the dispatcher itself has NO direct table SELECT privilege.
    EXECUTE format(
        'CREATE POLICY erasure_outbox_owner_dispatch_read ON public.erasure_outbox FOR SELECT TO %I USING (true)',
        schema_owner);
END
$owner_policy$;

CREATE FUNCTION public.omnia_read_erasure_outbox(
    p_batch_size integer,
    p_after_created_at timestamptz,
    p_after_id uuid
)
RETURNS TABLE(
    event_id uuid,
    tenant_id varchar(128),
    owner_sub varchar(255),
    event_type varchar(32),
    occurred_at timestamptz
)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fn$
BEGIN
    IF p_batch_size IS NULL OR p_batch_size < 1 OR p_batch_size > 100
        OR (p_after_created_at IS NULL) <> (p_after_id IS NULL) THEN
        RAISE EXCEPTION 'Invalid bounded outbox dispatch arguments'
            USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
        SELECT e.id, e.tenant_id, e.owner_sub, e.event_type, e.created_at
          FROM public.erasure_outbox AS e
         WHERE p_after_created_at IS NULL
            OR (e.created_at, e.id) > (p_after_created_at, p_after_id)
         ORDER BY e.created_at, e.id
         LIMIT p_batch_size;
END;
$fn$;

-- Function creation and REVOKE run in one migration transaction.
REVOKE ALL ON FUNCTION public.omnia_read_erasure_outbox(
    integer, timestamptz, uuid) FROM PUBLIC;
