-- CI-ONLY prototype of a recovery witness in a SECOND PostgreSQL 17 cluster.
-- No production signing, offsite storage, geo durability or legal retention.
-- Owner is the dedicated witness cluster's migration/admin role; never
-- grant table ownership or direct row privileges to runtime logins.
CREATE SCHEMA witness;
REVOKE ALL ON SCHEMA witness FROM PUBLIC;
CREATE TABLE witness.erasure_events (
    sequence bigint PRIMARY KEY CHECK (sequence > 0),
    event_id uuid NOT NULL UNIQUE,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    event_type varchar(32) NOT NULL CHECK (event_type = 'erase-actor'),
    witnessed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE witness.journal_head (
    singleton integer PRIMARY KEY CHECK (singleton = 1),
    next_sequence bigint NOT NULL CHECK (next_sequence > 0)
);
INSERT INTO witness.journal_head (singleton, next_sequence) VALUES (1,1);
REVOKE ALL ON ALL TABLES IN SCHEMA witness FROM PUBLIC;

-- An explicit, gapless COMMIT-ORDER counter, NOT nextval(). FOR UPDATE
-- serializes writers even when duplicate UUIDs race; rolled-back inserts
-- cannot consume a witness sequence. This still rolls back with a witness
-- cluster PITR and does NOT attest source outbox completeness.
CREATE FUNCTION witness.append_erasure(
    p_event_id uuid, p_tenant_id varchar, p_owner_sub varchar, p_event_type varchar
) RETURNS bigint
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
DECLARE
    v_next bigint;
    v_existing witness.erasure_events%ROWTYPE;
BEGIN
    IF p_event_id IS NULL OR p_event_id = '00000000-0000-0000-0000-000000000000'::uuid
        OR p_tenant_id IS NULL OR length(btrim(p_tenant_id)) = 0
        OR length(p_tenant_id) > 128 OR p_tenant_id ~ '[[:cntrl:]]'
        OR p_owner_sub IS NULL OR length(btrim(p_owner_sub)) = 0
        OR length(p_owner_sub) > 255 OR p_owner_sub ~ '[[:cntrl:]]'
        OR p_event_type IS DISTINCT FROM 'erase-actor' THEN
        RAISE EXCEPTION 'Invalid witness erasure event' USING ERRCODE = '22023';
    END IF;

    SELECT next_sequence INTO v_next FROM witness.journal_head
        WHERE singleton = 1 FOR UPDATE;
    IF NOT FOUND OR v_next IS NULL OR v_next >= 9223372036854775807 THEN
        RAISE EXCEPTION 'Witness head unavailable' USING ERRCODE = '55000';
    END IF;
    -- Recheck AFTER acquiring the same transaction lock, so concurrent
    -- retries cannot increment the sequence or issue duplicate events.
    SELECT * INTO v_existing FROM witness.erasure_events WHERE event_id = p_event_id;
    IF FOUND THEN
        IF v_existing.tenant_id <> p_tenant_id
           OR v_existing.owner_sub <> p_owner_sub
           OR v_existing.event_type <> p_event_type THEN
            RAISE EXCEPTION 'Conflicting witness event UUID' USING ERRCODE = '23505';
        END IF;
        RETURN v_existing.sequence;
    END IF;

    INSERT INTO witness.erasure_events
        (sequence, event_id, tenant_id, owner_sub, event_type)
    VALUES (v_next, p_event_id, p_tenant_id, p_owner_sub, p_event_type);
    UPDATE witness.journal_head SET next_sequence = v_next + 1 WHERE singleton = 1;
    RETURN v_next;
END
$body$;

CREATE FUNCTION witness.find_erasure(p_event_id uuid)
RETURNS TABLE(
    sequence bigint, event_id uuid, tenant_id varchar(128),
    owner_sub varchar(255), event_type varchar(32)
)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
    SELECT e.sequence, e.event_id, e.tenant_id, e.owner_sub, e.event_type
      FROM witness.erasure_events AS e WHERE e.event_id = p_event_id;
$body$;

CREATE FUNCTION witness.list_erasures(p_after_sequence bigint, p_limit integer)
RETURNS TABLE(
    sequence bigint, event_id uuid, tenant_id varchar(128),
    owner_sub varchar(255), event_type varchar(32)
)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
BEGIN
    IF p_after_sequence IS NULL OR p_after_sequence < 0
       OR p_limit IS NULL OR p_limit < 1 OR p_limit > 100 THEN
        RAISE EXCEPTION 'Invalid witness read bound' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
       SELECT e.sequence, e.event_id, e.tenant_id, e.owner_sub, e.event_type
       FROM witness.erasure_events AS e
       WHERE e.sequence > p_after_sequence
       ORDER BY e.sequence LIMIT p_limit;
END
$body$;

REVOKE ALL ON FUNCTION witness.append_erasure(uuid,varchar,varchar,varchar) FROM PUBLIC;
REVOKE ALL ON FUNCTION witness.find_erasure(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION witness.list_erasures(bigint,integer) FROM PUBLIC;
GRANT USAGE ON SCHEMA witness TO omnia_witness_writer, omnia_witness_reader;
GRANT EXECUTE ON FUNCTION witness.append_erasure(uuid,varchar,varchar,varchar)
    TO omnia_witness_writer;
GRANT EXECUTE ON FUNCTION witness.find_erasure(uuid),
    witness.list_erasures(bigint,integer) TO omnia_witness_reader;
