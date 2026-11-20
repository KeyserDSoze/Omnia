-- #141/#31: transactional outbox for erasure, in the primary DB only.
-- Neither an offsite ledger nor a monotonic externally trusted checkpoint.
CREATE TABLE public.erasure_outbox (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    event_type varchar(32) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_erasure_outbox_type CHECK (event_type = 'erase-actor')
);
CREATE INDEX ix_erasure_outbox_dispatch
    ON public.erasure_outbox (created_at, id);

ALTER TABLE public.erasure_outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.erasure_outbox FORCE ROW LEVEL SECURITY;
CREATE POLICY erasure_outbox_scoped_insert ON public.erasure_outbox
    FOR INSERT TO PUBLIC
    WITH CHECK (
        tenant_id = NULLIF(current_setting('omnia.tenant_id', true), '')
        AND owner_sub = NULLIF(current_setting('omnia.owner_sub', true), '')
    );

REVOKE ALL ON TABLE public.erasure_outbox FROM PUBLIC;
