CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS memory_entries (
    id uuid PRIMARY KEY,
    tenant_id varchar(128) NOT NULL,
    owner_sub varchar(255) NOT NULL,
    source varchar(120) NOT NULL,
    external_id varchar(255) NOT NULL,
    content_type varchar(120) NOT NULL,
    raw_content text NOT NULL,
    normalized_json jsonb NOT NULL,
    summary text NOT NULL,
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    embedding vector(768),
    embedding_model text,
    CONSTRAINT uq_memory_external UNIQUE (tenant_id, owner_sub, source, external_id)
);
CREATE INDEX IF NOT EXISTS ix_memory_recent
    ON memory_entries (tenant_id, owner_sub, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_memory_tags ON memory_entries USING gin (tags);
CREATE INDEX IF NOT EXISTS ix_memory_vector ON memory_entries USING hnsw (embedding vector_cosine_ops);
