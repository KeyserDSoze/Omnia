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


## Gate CI successivo al PITR — replay sicuro di cancellazione sintetica (ADR 0018)

Il test PITR ora può usare una ricevuta di cancellazione **fuori dal
backup PostgreSQL**: viene prodotta soltanto dopo il DELETE sintetico
sul sorgente, in una directory privata temporanea del runner. La
ricevuta JSON v1 è autenticata con HMAC-SHA256 e una chiave casuale
non presente nel PGDATA ripristinato. Non compare nei log o artifacts.

Il database fisico recupera inizialmente la nota anteriore al restore
point. Prima del replay, due tentativi con MAC assente o alterato
devono fallire senza effettuare cancellazioni. Il replay autenticato
usa poi `PostgresPersonalDataService.EraseAsync` con actor lock e RLS;
un secondo replay è idempotente, e export/timeline dell'attore cancellato
sono vuoti. Un secondo tenant sintetico rimane invece invariato.

Comandi CI-only dell'harness: `seed-peer`, `sign-receipt`, `reconcile`,
`verify-erased`, `verify-peer`; sono ristretti ai database di test
loopback e agli attori sintetici hard-coded. Il semplice possesso del
file JSON non è una credenziale di autorizzazione per gli utenti reali.

**Limitazioni importanti:** il signer e il verifier girano sullo stesso
runner e non provano l'origine, la durata o la completezza di un journal
esterno: mancano watermark monotono, firme da vault/HSM, documentazione
del diritto di cancellazione, ledger indipendente dai backup, policy di
riapertura del traffico, erasure di cloud object store, connettori e
provider e audit delle copie storiche. Questo è soltanto un test di
replay nell'ambito usa-e-getta: NON riutilizzarlo in produzione.

[ADR 0018](../adr/0018-synthetic-post-pitr-erasure-replay.md).


## Gate applicativo di quarantena HTTP post-PITR (#141, ADR 0019)

L'API ora legge **prima delle migrazioni** un file amministrativo indicato
da `Recovery__AccessStateFile`. In qualunque ambiente non Development
il file è obbligatorio e deve risiedere su un mount riservato, esterno
al PGDATA recuperato e ai backup. Non incorporarlo in immagini Docker,
file Git o artifact della CI. Se il file manca l'API non si avvia.

Il file deve essere regolare, non symlink, con accesso esclusivo del
proprietario (`chmod 400` o `chmod 600`), massimo 32 byte.
Il contenuto è rigorosamente `ready` seguito da LF (newline);
`quarantined` con LF impedisce l'avvio, così come qualsiasi formato
non riconosciuto. Preparare e sostituire il file in modo atomico
attraverso il sistema di configurazione esterno. Il codice non
implementa da solo la custodia del marker o l'autorizzazione dell'operatore.

Se l'API è già attiva, prima di ogni richiesta HTTP viene ricontrollato
il file. Revoca, rimozione o corruzione restituiscono HTTP 503 con
`Cache-Control: no-store` e JSON `recovery_quarantined` su /health
e /api/v1. Il workflow OIDC E2E prova il blocco in un server reale.
In Development, solo l'assenza della configurazione è consentita;
un percorso fornito ma non valido fallisce comunque.

ATTENZIONE: `ready` non attesta alcuna cancellazione, non è firmato
e non dimostra che tutte le ricevute post-PITR siano state rilevate.
Un amministratore in errore può bypassare questa misura impostando ready.
Il gate di questa singola API NON ferma retention worker già attivi,
offline backfill, ingress di altre repliche o richieste in volo.
Non riaprire servizi con dati reali prima del journal anti-rollback
e della quarantena globale di #141. ADR: [0019](../adr/0019-api-recovery-quarantine.md).


## In-process retention worker: quarantena tra batch (#141 / ADR 0020)

Anche il worker `RetentionSweepService`, eseguito nello stesso processo
ASP.NET, ora controlla `Recovery__AccessStateFile` prima di **ogni batch**
di cancellazione TTL. Se manca o indica quarantena, non effettua
ulteriori chiamate a `omnia_purge_expired_memories`; riprende soltanto
durante un ciclo successivo quando il marker torna valido `ready` + LF.
Il ruolo DB è sempre il login non privilegiato `omnia_retention`.

I test PostgreSQL verificano che una nota scaduta rimanga fisicamente
nel DB durante la quarantena e venga eliminata soltanto dopo il
rilascio del marker, senza toccare una nota ancora viva. Un altro
test simula la revoca dopo il primo batch autorizzato e controlla
che il secondo non venga lanciato.

**Non è un fence transazionale**: se il file cambia mentre una query
PostgreSQL è già in corso, essa può arrivare al commit. Non sono
recintati DB access esterni, offline backfill, altre repliche o
l'eventuale lavoro verso provider. La release non è firmata e non
verifica un journal anti-rollback. Per questi gap #141 resta aperta.

Decisione: [ADR 0020](../adr/0020-recovery-gated-retention-worker.md).


## Audit offline di journal + checkpoint dopo PITR (#141 / ADR 0021)

`src/backend/Omnia.RecoveryAudit` è un tool **solo lettura**:
non si connette a PostgreSQL, non elimina dati e non cambia il file
`Recovery__AccessStateFile`. Richiede file **assoluti, privati e
separati dalla copia PGDATA** indicati tramite:

- `OMNIA_RECOVERY_JOURNAL_PATH`: JSON Lines v1 con eventi erase-actor;
- `OMNIA_RECOVERY_CHECKPOINT_PATH`: checkpoint v1 con numero e tip SHA-256;
- `OMNIA_RECOVERY_JOURNAL_KEY_PATH`: chiave HMAC binaria casuale da 32 byte;
- `OMNIA_RECOVERY_CHECKPOINT_KEY_PATH`: seconda chiave HMAC distinta.

Controlla LF framing, firme HMAC-SHA256, catena canonica, sequenze
contigue, UUID unici, schema senza duplicati, dimensioni e coerenza
tra conteggio finale e checkpoint. Un journal troncato viene rifiutato
se il checkpoint esterno non è stato a sua volta riportato indietro.
Rifiuta file mancanti, symlink, group/world read e formati ignoti.
Exit 0 = file verificati contro il checkpoint PRESENTE; exit 2 = errore.
Il tool non stampa tenant, owner, evento o chiavi; solo un totale.

**Non è ancora una procedura reale di emissione e approvazione**:
non esistono producer di cancellazioni su bus/outbox durabile,
storage immutabile o ancoraggio monotono da Azure/OpenBao/HSM.
La chiave e il checkpoint devono essere forniti dall'operatore
indipendentemente dal backup, non creati durante la recovery stessa.
Se vengono troncati o ripristinati insieme al journal, il verificatore
non può attestare la completezza globale. Il risultato non promuove
l'API, non esegue `EraseAsync` e non soddisfa il gate #141.

Vedi [ADR 0021](../adr/0021-recovery-journal-checkpoint-verifier.md).


### Collegamento con il PITR sintetico (ADR 0021)

Lo script `tests/restore/physical-pitr-rehearsal.sh` genera ora
**nel runner usa-e-getta**, dopo la cancellazione della fixture,
un singolo evento erase-actor HMAC, un checkpoint con chiave separata
e due file binari delle chiavi. La CLI offline li verifica prima
del replay HMAC della ricevuta descritto in ADR 0018. Il test svuota
poi il journal e richiede il rifiuto del troncamento senza cambiare
il checkpoint, prima di ripristinare il file e proseguire.

Questo dimostra il wiring in CI del verificatore e del checkpoint
su dati sintetici; la generazione avviene **nello stesso runner**
e non fornisce durabilità, autenticazione di un operatore esterno
o protezione contro rollback simultaneo di journal e checkpoint.
Il tool non autorizza `ready`, non ferma servizi e non cancella dati.
