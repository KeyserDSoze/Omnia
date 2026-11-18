-- NEW memory captures must be sealed at application layer. Existing rows
-- are NOT rewritten by SQL: a separate, audited backfill is necessary (#138).
-- Do not put encryption keys inside the database or a migration.
ALTER TABLE public.memory_entries ADD COLUMN sealed_payload bytea;

ALTER TABLE public.memory_entries
ADD CONSTRAINT ck_memory_sealed_content_redacted CHECK (
    sealed_payload IS NULL OR
    (raw_content = '[sealed]'
      AND normalized_json = '{}'::jsonb
      AND summary = '[sealed]'
      AND tags = ARRAY[]::text[])
);

-- Reject old application binaries that still attempt plaintext INSERT.
-- Historical rows predate this migration and remain readable (until backfill).
-- Updating metadata (e.g., expiry) of a historical row is allowed, but
-- changes to any sensitive content require an authenticated sealed payload.
CREATE FUNCTION public.omnia_enforce_memory_sealed_writes()
RETURNS trigger LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NEW.sealed_payload IS NULL THEN
        IF TG_OP = 'INSERT' THEN
            RAISE EXCEPTION 'Memory encryption is mandatory on new writes'
                USING ERRCODE = '23514';
        END IF;
        IF OLD.sealed_payload IS NOT NULL
            OR NEW.raw_content IS DISTINCT FROM OLD.raw_content
            OR NEW.normalized_json IS DISTINCT FROM OLD.normalized_json
            OR NEW.summary IS DISTINCT FROM OLD.summary
            OR NEW.tags IS DISTINCT FROM OLD.tags THEN
            RAISE EXCEPTION 'Unsealed mutation of sensitive memory is forbidden'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_memory_sealed_writes
BEFORE INSERT OR UPDATE ON public.memory_entries
FOR EACH ROW EXECUTE FUNCTION public.omnia_enforce_memory_sealed_writes();

REVOKE ALL ON FUNCTION public.omnia_enforce_memory_sealed_writes() FROM PUBLIC;
