-- CI-ONLY third PostgreSQL 17 cluster. Must never share PGDATA or roles
-- with primary/witness. This simulates a separately surviving checkpoint
-- store, NOT an offsite/geo-independent monotonic/WORM authority.
BEGIN;
CREATE SCHEMA anchor;
REVOKE ALL ON SCHEMA anchor FROM PUBLIC;
CREATE TABLE anchor.checkpoints (
    checkpoint_count integer PRIMARY KEY CHECK (checkpoint_count BETWEEN 1 AND 10000),
    checkpoint bytea NOT NULL CHECK (octet_length(checkpoint) BETWEEN 100 AND 4096),
    committed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE anchor.head (
    singleton integer PRIMARY KEY CHECK (singleton = 1),
    checkpoint_count integer NOT NULL DEFAULT 0 CHECK (checkpoint_count BETWEEN 0 AND 10000)
);
INSERT INTO anchor.head(singleton, checkpoint_count) VALUES (1, 0);
REVOKE ALL ON ALL TABLES IN SCHEMA anchor FROM PUBLIC;

-- A single serialized, nonregressing checkpoint. A privileged DB owner
-- can still revert the whole anchor PGDATA, and this routine does not
-- know the signing key. The caller MUST independently authenticate
-- the previous checkpoint against the witness before advancing.
CREATE FUNCTION anchor.advance_checkpoint(p_count integer, p_payload bytea)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
DECLARE
    v_old_count integer;
    v_previous bytea;
BEGIN
    IF p_count IS NULL OR p_count NOT BETWEEN 1 AND 10000
        OR p_payload IS NULL OR octet_length(p_payload) NOT BETWEEN 100 AND 4096 THEN
        RAISE EXCEPTION 'Invalid checkpoint input' USING ERRCODE='22023';
    END IF;
    SELECT checkpoint_count INTO v_old_count
      FROM anchor.head WHERE singleton = 1 FOR UPDATE;
    IF NOT FOUND OR v_old_count IS NULL THEN
        RAISE EXCEPTION 'Anchor head missing' USING ERRCODE='55000';
    END IF;
    IF p_count < v_old_count THEN
        RAISE EXCEPTION 'Anchor checkpoint regression' USING ERRCODE='23514';
    END IF;
    IF p_count = v_old_count THEN
        SELECT checkpoint INTO v_previous FROM anchor.checkpoints
         WHERE checkpoint_count = v_old_count;
        IF v_previous IS DISTINCT FROM p_payload THEN
            RAISE EXCEPTION 'Anchor checkpoint conflict' USING ERRCODE='23505';
        END IF;
        RETURN v_old_count;
    END IF;
    INSERT INTO anchor.checkpoints (checkpoint_count, checkpoint)
        VALUES (p_count, p_payload);
    UPDATE anchor.head SET checkpoint_count = p_count
        WHERE singleton = 1;
    RETURN p_count;
END
$body$;

CREATE FUNCTION anchor.current_checkpoint()
RETURNS TABLE(checkpoint_count integer, checkpoint bytea)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
    SELECT c.checkpoint_count, c.checkpoint
      FROM anchor.head h
      JOIN anchor.checkpoints c ON c.checkpoint_count = h.checkpoint_count
     WHERE h.singleton = 1;
$body$;

REVOKE ALL ON FUNCTION anchor.advance_checkpoint(integer,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION anchor.current_checkpoint() FROM PUBLIC;
GRANT USAGE ON SCHEMA anchor TO omnia_anchor_writer, omnia_anchor_reader;
GRANT EXECUTE ON FUNCTION anchor.advance_checkpoint(integer,bytea) TO omnia_anchor_writer;
GRANT EXECUTE ON FUNCTION anchor.current_checkpoint() TO omnia_anchor_reader;
COMMIT;
