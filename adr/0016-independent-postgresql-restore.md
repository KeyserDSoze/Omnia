# ADR 0016 — Restore del backup logico in cluster PostgreSQL indipendente

**Data:** 2026-11-19
**Stato:** Accepted per test sintetico in GitHub Actions (#109, #140); non è una procedura DR di produzione.

## Contesto

ADR 0015 verifica `pg_dump -Fc` e `pg_restore` in due database dello
stesso cluster usa-e-getta. Tale scenario non esercita il ripristino dei
ruoli globali, il cambio credenziali né l'indipendenza del data directory.
Per un recovery più credibile serve un nuovo PostgreSQL inizializzato
separatamente. La documentazione PostgreSQL precisa che `pg_dump` NON
include gli oggetti globali, come i ruoli, e che gli ACL devono riferirsi
a ruoli effettivamente esistenti nel cluster target.

## Decisione

- La CI conserva la prova ADR 0015 e, subito dopo, avvia il secondo
  container temporaneo `pgvector/pgvector:pg17`, con nuovo PGDATA,
  distinto `system_identifier` controllato via `pg_control_system()`,
  nuova porta loopback `15433`, database `omnia_recovery` e password
  amministrativa e applicativa tutte generate ex novo.
- Si trasferisce un archivio `pg_dump -Fc` in una directory temporanea
  mode 0700 sul runner; backup file mode 0600, nessun artifact pubblico.
  Lo script non opera su dati/indirizzi di produzione.
- Si controlla prima che il cluster target non contenga i quattro
  ruoli Omnia. Un `pg_restore --no-owner --exit-on-error` con ACL e
  ruoli mancanti DEVE fallire. Si elimina il database target parziale,
  poi si ricrea un DB vuoto e si provisionano esplicitamente
  `omnia_app`, `omnia_retention`, `omnia_backfill` e
  `omnia_backfill_audit` con identità non-superuser/NOBYPASSRLS,
  password nuove e senza membership nel ruolo owner.
- Non si importa automaticamente un dump `pg_dumpall --globals-only`,
  che potrebbe includere credenziali/privilegi superuser da un ambiente
  differente. Il replay effettua `pg_restore --no-owner --exit-on-error`
  come proprietario schema nel solo container di test.
- Un check negativo prova che la vecchia password `omnia_app` non
  autentichi sul target. La suite .NET usa soltanto `omnia_app`
  e accetta esclusivamente 127.0.0.1:15432/15433 e nomi DB sintetici.
- Verifica nel target: ruoli e RLS FORCE, query senza actor scope non
  vedono righe su `memory_entries` né `consent_grants`; timeline per
  altro actor vuota; UUID, sealed AEAD, AAD, metadata, TTL, vettori
  ed export invariati. La KEK storica è obbligatoria anche se il
  nuovo ring ha un activeKeyId differente; senza di essa fail-closed.
- Cancella il solo dato ripristinato, verifica che il sorgente esista
  ancora e distrugge container, dump e keyring mediante trap EXIT.

## Conseguenze e limiti

Questa è una verifica reale di **due cluster PostgreSQL distinti**
ma entrambi locali e usa-e-getta nello stesso runner CI, con
backup **logico** e dati esclusivamente sintetici. Non dimostra
network isolation, backup cifrati, ruolo di migrazione non-superuser
su Azure, backup fisico, PITR/WAL, replica, tombstones, restore di
segreti da vault, conservazione backup o recovery dei servizi esterni.
Un dump logico può includere plaintext storico, embeddings e metadati.
Il restore non è un certificato GDPR, né abilita
`sealed_payload NOT NULL` o il cutover legacy. #109/#140/#138/#33 restano OPEN.

## Fonti PostgreSQL ufficiali

- https://www.postgresql.org/docs/17/app-pg-dumpall.html
- https://www.postgresql.org/docs/17/app-pgdump.html
- https://www.postgresql.org/docs/17/app-pgrestore.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html