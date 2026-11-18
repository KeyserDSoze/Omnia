-- One narrowly scoped, provisional policy for the current PostgreSQL-only notes.
-- The GDPR does not prescribe a universal 30-day period: see ADR 0009.
-- For historic rows use ORIGINAL server received_at (never migration time).
ALTER TABLE public.memory_entries ADD COLUMN expires_at timestamptz;
UPDATE public.memory_entries
SET expires_at = received_at + interval '30 days';
ALTER TABLE public.memory_entries ALTER COLUMN expires_at SET NOT NULL;
ALTER TABLE public.memory_entries ALTER COLUMN expires_at
    SET DEFAULT (now() + interval '30 days');

CREATE INDEX ix_memory_expiry ON public.memory_entries (expires_at, id);

-- The API runtime must NOT be able to invoke this function.
-- Only the dedicated, no-bypass retention worker receives EXECUTE in the migrator.
-- The function is owned by the schema migration identity and is deliberately
-- SECURITY DEFINER; it has a fixed SQL body, no user-defined predicates,
-- strict batch bounds, and uses only fully qualified tables.
CREATE FUNCTION public.omnia_purge_expired_memories(batch_size integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    affected integer;
BEGIN
    IF batch_size IS NULL OR batch_size < 1 OR batch_size > 500 THEN
        RAISE EXCEPTION 'Invalid retention batch size'
            USING ERRCODE = '22023';
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

REVOKE ALL ON FUNCTION public.omnia_purge_expired_memories(integer) FROM PUBLIC;
