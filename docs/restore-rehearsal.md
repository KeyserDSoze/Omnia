# Synthetic restore rehearsal (issue #140)

**CI test only. Never execute against user or production data.**

The Omnia CI job starts a disposable pgvector PostgreSQL 17 container and
creates random DB passwords and an ephemeral AES-GCM keyring outside Git.
After the real PostgreSQL xUnit suite, it runs:

```sh
bash tests/restore/restore-rehearsal.sh
```

The script requires the CI container omnia-ci-pg, OMNIA_TEST_APP_DB and
OMNIA_TEST_KEYRING_FILE. The harness executable only connects to
127.0.0.1:15432, user omnia_app, and database omnia_test or
omnia_restore_drill. It will not connect to an arbitrary production host.

1. Seed a synthetic note, consent and 768-dimensional vector via the
   non-owner runtime role; verify the note is already sealed.
2. Perform a real custom-format pg_dump into a protected file within
   the disposable CI DB container; createdb and pg_restore to a second
   database, with restored schema, triggers, RLS and privileges.
3. Keep the old KEK in a keyring with a different active key. Verify
   ciphertext, canonical record ID/AAD, redaction, TTL, vector,
   RLS-protected timeline and JSON export after restore.
4. Re-run with a ring lacking the old KEK and demand cryptographic
   failure rather than returning redacted placeholders or fabricated data.
5. Erase the clone only and re-read the original source record. Remove
   clone and secret files via EXIT trap, including failed runs.

The dump and temporary keys are NOT uploaded as CI artifacts. A dump
can still contain plaintext legacy rows or sensitive metadata; separate
backup encryption, key custody and retention policies remain required.

**Limitations:** same-cluster logical restore, not a physical PITR/WAL
recovery, not a separate cluster's role re-provisioning, not Azure
Flexible Server, not vault recovery, and not erasure of archived copies.
Do not remove historical KEKs or enforce sealed_payload NOT NULL based
on this CI-only result. Parent #138/#33 and #140 remain open.

Decision: [ADR 0015](../adr/0015-synthetic-postgresql-restore-rehearsal.md).