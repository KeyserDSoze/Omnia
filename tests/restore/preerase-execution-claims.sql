-- CI-ONLY, separate database on THIRD PostgreSQL 17 PGDATA.
-- The recorded fact is "an attempt was claimed BEFORE source DELETE";
-- NOT "source DELETE committed", NOT a completed-erasure witness receipt.
-- Do not use for a production GDPR deletion or authorization decision.
BEGIN;
CREATE SCHEMA admission;
REVOKE ALL ON SCHEMA admission FROM PUBLIC;
CREATE TABLE admission.execution_claims (
    request_id uuid PRIMARY KEY
        CHECK (request_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    scope_hmac bytea NOT NULL CHECK (octet_length(scope_hmac) = 32),
    claimed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
REVOKE ALL ON admission.execution_claims FROM PUBLIC;

-- INSERT and UUID PK are the single-consumption barrier. A duplicate
-- request MUST raise SQLSTATE 23505; even an identical repeat is refused.
-- The transaction commits on the THIRD PGDATA before source DELETE begins.
CREATE FUNCTION admission.claim_once(p_id uuid, p_scope_hmac bytea)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $function$
BEGIN
    IF p_id IS NULL OR
       p_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_scope_hmac IS NULL OR octet_length(p_scope_hmac) <> 32 THEN
        RAISE EXCEPTION 'Invalid pre-erase attempt' USING ERRCODE='22023';
    END IF;
    INSERT INTO admission.execution_claims(request_id,scope_hmac)
    VALUES (p_id,p_scope_hmac);
    RETURN true;
END
$function$;

CREATE FUNCTION admission.has_claim(p_id uuid)
RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $function$
    SELECT EXISTS (
        SELECT 1 FROM admission.execution_claims
        WHERE request_id = p_id
    );
$function$;

-- Only the restricted reader can retrieve an individual persisted scope
-- HMAC. The reader verifies it in constant time with a private signing key;
-- the writer MUST NOT be able to query it, and neither role can SELECT rows.
-- A missing UUID yields SQL NULL: NEVER interpret it as "not committed".
CREATE FUNCTION admission.claim_scope(p_id uuid)
RETURNS bytea LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $function$
    SELECT scope_hmac FROM admission.execution_claims
     WHERE request_id = p_id;
$function$;

-- REVOKE and GRANT belong to the SAME transaction as CREATE FUNCTION
-- to avoid the default PUBLIC EXECUTE gap of SECURITY DEFINER.
REVOKE ALL ON FUNCTION admission.claim_once(uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION admission.has_claim(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION admission.claim_scope(uuid) FROM PUBLIC;
GRANT USAGE ON SCHEMA admission TO omnia_claim_writer, omnia_claim_reader;
GRANT EXECUTE ON FUNCTION admission.claim_once(uuid,bytea)
    TO omnia_claim_writer;
GRANT EXECUTE ON FUNCTION admission.has_claim(uuid)
    TO omnia_claim_reader;
GRANT EXECUTE ON FUNCTION admission.claim_scope(uuid)
    TO omnia_claim_reader;
COMMIT;
