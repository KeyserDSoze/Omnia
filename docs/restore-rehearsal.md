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

## #109/#140 — Restore in un secondo cluster usa-e-getta (ADR 0016)

Il passo ulteriore rispetto al test ADR 0015 è
`tests/restore/cross-cluster-rehearsal.sh`, lanciato dalla stessa CI
DOPO il restore sintetico al database `omnia_restore_drill`.

Il job avvia `omnia-ci-pg-recovery` su `127.0.0.1:15433` con database
`omnia_recovery`, una nuova directory PGDATA e nuove password random.
Confronta `system_identifier` dei due cluster PostgreSQL per evitare
una falsa separazione. Un restore con i ruoli Omnia mancanti deve fallire;
la CI quindi ricrea il database e provisiona i quattro login limitati
senza copiare i segreti globali del server sorgente. Solo allora esegue
il vero `pg_restore` dell'archivio logico.

L'harness `Omnia.RestoreRehearsal` effettua i check anche nel cluster
secondario come identità `omnia_app`: nessuna visibilità SQL senza
contesto RLS su note/consensi, ID canonico, integrità AES-GCM,
embedding, ricevuto/scadenza, timeline, export e erase. La password
del login originario è rifiutata nel cluster di ripristino. Il test
prova la decifratura mantenendo la vecchia KEK e il fallimento quando
essa non è presente. L'erase nel target non modifica il sorgente.

La cartella privata nel runner e i backup/secret file sono distrutti
anche in caso di errore, insieme al secondo container. Nessun dump
o keyring viene inviato come artifact.

**Attenzione:** entrambi i cluster sono container locali di CI
nello stesso runner; non è un ambiente Azure o geograficamente isolato.
Resta un backup **logico**, non WAL/PITR. Non testa restore del vault,
repliche, object storage, backup/tombstones GDPR o decrittazione di
tutti i record legacy. Non è una procedura utilizzabile per dati reali.

Decisione: [ADR 0016](../adr/0016-independent-postgresql-restore.md).
Documentazione ufficiale:
https://www.postgresql.org/docs/17/app-pg-dumpall.html
https://www.postgresql.org/docs/17/app-pgrestore.html


## Terzo gate CI — basebackup fisico e point-in-time recovery (ADR 0017)

Dopo la prova di restore logico sul secondo cluster (ADR 0016), la CI
invoca `bash tests/restore/physical-pitr-rehearsal.sh` in un ambiente
**totalmente usa-e-getta**: il PostgreSQL 17/pgvector originario archivia
i segmenti WAL solo in una directory temporanea interna al container.

La fase esegue `pg_basebackup -Fp -X stream` e `pg_verifybackup`, poi
registra un evento marker **successivo al backup**, crea un restore point
PostgreSQL nominale, effettua una cancellazione di una **nota sintetica**
nel sorgente e scrive un secondo marker. Una forzatura `pg_switch_wal()`
consente di attendere l'archiviazione del segmento richiesto.

Un nuovo container su `127.0.0.1:15435` parte dal PGDATA della basebackup,
legge il WAL archiviato via `restore_command`, incontra il
`recovery_target_name`, viene promosso e deve contenere il primo marker
ma non il secondo. La nota cancellata sul sorgente deve invece essere
ancora accessibile nel recuperato: la prova verifica il confine temporale
effettivo, non solamente un database che si avvia.

Il processo controlla `system_identifier` invariato per una copia
**fisica**, ruota la password `omnia_app` nel recuperato e rifiuta la
precedente. Esegue l'harness esistente sulla nota ripristinata: RLS,
contenuto AEAD, UUID/AAD, expiry, embedding e export; testa anche la
mancanza della vecchia KEK e la cancellazione della copia.

Le copie temporanee risiedono nel runner e in un volume Docker
usa-e-getta; il cleanup le elimina anche su errore. **Nessun dump
fisico o WAL viene reso pubblico**. La keyring non è nel backup
PostgreSQL e deve essere mantenuta esternamente.

**NON è un restore Azure o una procedura di DR production-ready**:
mancano backup cifrati/persistenti, copie geografiche, policy RPO/RTO,
monitoraggio archivio, Azure Flexible Server, vault recovery, gestione
deleted-data/tombstones, enforcement delle retention e verifica dei
diritti GDPR sulle copie storiche. Fino a questi gate #109/#140 restano OPEN.

ADR: [0017](../adr/0017-physical-postgresql-pitr.md).
