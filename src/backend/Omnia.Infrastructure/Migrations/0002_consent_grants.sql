-- Scoped authorization records. The user-facing acknowledgement alone is not a legal
-- basis for processing conversations or recordings involving other people.
CREATE TABLE consent_grants (
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

-- Exactly one unrevoked grant per tenant/owner/source/purpose; a new grant
-- supersedes the prior grant in a transaction, preserving the revoked history.
CREATE UNIQUE INDEX uq_consent_active_scope ON consent_grants
    (tenant_id, owner_sub, source, purpose) WHERE revoked_at IS NULL;
CREATE INDEX ix_consent_history ON consent_grants
    (tenant_id, owner_sub, granted_at DESC);
