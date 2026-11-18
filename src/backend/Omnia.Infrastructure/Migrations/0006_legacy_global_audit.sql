-- #140 first global verification slice: exact count only, no tenant identifiers,
-- payload, sample IDs, or mutable inputs. Never wire this to a public API.
-- FORCE RLS applies to a non-superuser table owner: explicitly permit only
-- the trusted schema owner to SELECT for this SECURITY DEFINER computation.
-- The migration role is already privileged to alter RLS; never grant this
-- policy to API, backfill, retention, or audit roles.
DO $policy$
DECLARE
    schema_owner name;
BEGIN
    SELECT pg_get_userbyid(relowner) INTO schema_owner
      FROM pg_class WHERE oid = 'public.memory_entries'::regclass;
    IF schema_owner IS NULL OR schema_owner <> current_user THEN
        RAISE EXCEPTION 'Global legacy count migration requires the table owner'
            USING ERRCODE = '42501';
    END IF;
    EXECUTE format(
        'CREATE POLICY memory_schema_owner_count ON public.memory_entries FOR SELECT TO %I USING (true)',
        schema_owner);
END
$policy$;

-- Fixed, parameter-free SQL body and locked-down search_path.
-- Returns an EXACT count, including expired legacy rows not yet purged.
-- Schema migration + REVOKE run in the SAME transaction, avoiding
-- a public EXECUTE window. Only the dedicated audit role gets EXECUTE.
CREATE FUNCTION public.omnia_count_legacy_memories()
RETURNS bigint
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $function$
    SELECT count(*)::bigint FROM public.memory_entries
    WHERE sealed_payload IS NULL;
$function$;

REVOKE ALL ON FUNCTION public.omnia_count_legacy_memories() FROM PUBLIC;
