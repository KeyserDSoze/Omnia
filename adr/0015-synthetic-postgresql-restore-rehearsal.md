# ADR 0015 — Logical PostgreSQL restore rehearsal with synthetic sealed memory

**Data:** 2026-11-18. **Stato:** Accepted for a CI-only slice of #140, NOT a production restore sign-off.

## Context

Codec roundtrips alone do not establish that a real PostgreSQL backup restores
sealing, RLS, canonical IDs, expiry, export and historical encryption keys.
PostgreSQL's own pg_dump and pg_restore support a custom-format logical dump.

## Decision

1. Use only the disposable pgvector/pgvector:pg17 CI container, random
   per-run DB passwords and ephemeral keyring. The fixture uses synthetic
   content, source-scoped consent and non-owner runtime RLS.
2. Seed a sealed note through PostgresMemoryRepository, then run pg_dump -Fc
   inside the disposable DB container. Restore to an independent DATABASE
   in the same cluster with pg_restore --exit-on-error --no-owner.
3. Use a ring with changed active KEK but retaining the historical KEK.
   Verify original AAD/UUID, decrypted raw/normalized/summary/tags,
   vector/model, server TTL, RLS timeline and complete export after restore.
4. Attempt to open the same envelope with only the new KEK: it must fail
   closed; the DB snapshot does not carry the key required to decrypt.
5. Erase the restored clone through the scoped service, then verify the
   source still contains and decrypts the original fixture.
6. Do not upload backup or keys as artifacts. Clean temporary files and
   clone database even on failure. CLI rejects arbitrary hosts, admin
   credentials and non-test database names; no HTTP endpoint.

## Limits / non-goals

This is one **logical restore to another database in the SAME cluster**.
It does not verify cross-cluster role provisioning, physical backups,
WAL/PITR, Azure managed backups, vault/KEK recovery, encrypted archives,
propagation of GDPR erasure into prior backups, or production recovery.
Embeddings, metadata and any plaintext legacy rows in a backup remain
sensitive. No automatic sealed_payload NOT NULL cutover, and #140/#138/#33
remain OPEN pending the other gates.

## Official references

- https://www.postgresql.org/docs/17/app-pgdump.html
- https://www.postgresql.org/docs/17/app-pgrestore.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html