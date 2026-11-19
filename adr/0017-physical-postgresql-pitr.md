# ADR 0017 — Test CI di backup fisico e PITR WAL PostgreSQL 17

**Data:** 2026-11-19
**Stato:** Accepted come gate sintetico CI di #109/#140; non come piano di disaster recovery di produzione.

## Motivazione

ADR 0015–0016 verificano `pg_dump` e `pg_restore`, prima nello stesso cluster e poi in un secondo cluster. Un archivio **logico** non prova la catena di WAL o il ripristino a un istante precedente a una mutazione distruttiva. È necessaria una prova fisica e un *target* di recovery osservabile, non soltanto il boot PostgreSQL.

## Decisione e confini

- La CI usa solo `omnia-ci-pg` (PostgreSQL 17/pgvector usa-e-getta) già inizializzato con dati di test e chiavi generate casualmente nel runner. L'archiviazione WAL è abilitata con `archive_mode=on`, `archive_command` confinato in `/tmp/omnia-ci-wal-archive` del container, senza rete e senza servizi di produzione.
- Dopo il restore logico intercluster, esegue `pg_basebackup --format=plain --wal-method=stream --checkpoint=fast` e `pg_verifybackup` dentro il container di test. Il backup è una copia fisica completa del cluster, inclusi ruoli e configurazione; non si tratta di `pg_dump`.
- Soltanto **dopo** la conclusione del basebackup, crea una tabella marker, scrive `before-target`, registra `pg_create_restore_point('omnia_ci_before_destructive_change')`, elimina una nota sintetica sul sorgente, scrive `after-target` e invoca `pg_switch_wal()`. Attende che il segmento WAL sia effettivamente archiviato.
- Copia i file fisici e gli archivi WAL esclusivamente in una directory privata del runner e in un volume Docker usa-e-getta. Un nuovo container PostgreSQL sul loopback `127.0.0.1:15435` parte dalla copia fisica con `recovery.signal`, `restore_command` e `recovery_target_name` fissato, `recovery_target_action='promote'`.
- Il test deve trovare nel ripristinato la marker `before-target` ma NON `after-target`, recuperando contemporaneamente la nota eliminata sul sorgente. Questo dimostra la riproduzione di WAL **successivo al basebackup** e un arresto effettivo al punto desiderato.
- A differenza del restore logico (dove gli identificativi di sistema sono diversi), una copia fisica del cluster DEVE mantenere lo stesso `system_identifier` PostgreSQL. Si verifica la promozione come primario.
- Dato che un backup fisico trasferisce anche il catalogo dei ruoli, il login runtime viene **ruotato nel ripristinato** prima di autorizzare le verifiche applicative: le credenziali originali devono fallire. L'harness è vincolato ai database di test loopback, con login `omnia_app` non-owner e RLS forzata.
- L'applicazione verifica ciphertext e AAD, tenant/subject, UUID, TTL, vector, timeline e export. Una key ring senza la KEK storica deve fallire chiusa. La cancellazione finale agisce solo sulla copia ripristinata.
- Il cleanup distrugge container, volume, basebackup, copie WAL e ring temporaneo. Nessun backup/KEK viene committato, stampato o pubblicato negli artifact CI.

## Limiti operativi, privacy e rischi

Questo è **un test sintetico automatizzato su un unico runner**, non un backup o PITR gestito, geograficamente isolato o documentato con RPO/RTO/SLA. La copia fisica contiene anche il database primario e credenziali globali: la rotazione effettuata nel test NON sostituisce la rotazione e custodia dei segreti in produzione. L'archivio e la basebackup CI non sono cifrati come sarebbe necessario in produzione, e possono includere embedding, metadata e righe legacy non sealed. Il cleanup delle fixture non dimostra la cancellazione effettiva di vecchi backup legali o tombstone; non è una DPIA né certifica GDPR.

**Non autorizza** `sealed_payload NOT NULL`, rimozione fallback, release con dati reali, Azure Flexible Server, storage georeplicato, backup encryption, OpenBao/Key Vault, ripristino chiavi dal vault o gestione di WAL/backup in esercizio. #109, #140, #138 e #33 rimangono aperte.

## Fonti ufficiali

- https://www.postgresql.org/docs/17/continuous-archiving.html
- https://www.postgresql.org/docs/17/app-pgbasebackup.html
- https://www.postgresql.org/docs/17/runtime-config-wal.html
- https://www.postgresql.org/docs/17/functions-admin.html