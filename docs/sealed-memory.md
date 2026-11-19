# Note sealed in PostgreSQL — prima fase (#139, #138 / #33)

**Scopo:** cifratura applicativa di quattro campi dei **nuovi record** in `memory_entries`: `raw_content`, `normalized_json`, `summary`, `tags`. Il database conserva uno `sealed_payload bytea` autenticato; i campi precedenti nelle nuove righe sono redatti. Preesistenti legacy NON migrati restano in chiaro. **Non dichiarare che tutte le note sono criptate.**

## Schema e write path

`0005_memory_sealed_payload.sql` aggiunge la colonna e il vincolo `ck_memory_sealed_content_redacted`, più trigger `trg_memory_sealed_writes`. Un client/server vecchio che invia INSERT senza envelope riceve SQLSTATE `23514`; non può mantenere nuove note plaintext dopo il cutover. Modifiche ai campi sensibili delle righe legacy devono prima cifrarli; aggiornamenti ai soli metadati/expiry restano possibili. Ogni nuovo payload è un documento JSON serializzato e cifrato con `IEnvelopeEncryption.Seal` (AES-GCM v1, per-object DEK, key ring esterna). La AAD concatena in formato canonico lunghezza-prefissato il tenant e owner **derivati da JWT validato**, la finalità `memory.payload` e l'ID stabile della riga DB. `PostgresActorLock` garantisce che l'UPSERT trovi lo stesso UUID canonico dei retry prima di cifrare.

## Lettura, export, retention, erasure

La normale timeline decifra summary+tags in memoria *soltanto* tramite PostgresActorScope/RLS e nasconde comunque le note scadute. L'export autentica e decifra raw/normalized/summary/tags delle righe sealed, o legge i vecchi plaintext se `sealed_payload` è nullo. `storageProtection` segnala lo stato riga per riga; un ciphertext corrotto genera errore, mai valori inventati o fallback. Il worker #136 e l'API `/erase` eliminano tutte le colonne della riga, quindi il blob cifrato non sopravvive al DELETE nel DB principale. Nessuna propagazione verso backup o altri storage.

## Sviluppo e segreti

La API **richiede** `Crypto:KeyRingPath` come percorso file assoluto leggibile a boot. Usare il formato di [docs/envelope-encryption.md](envelope-encryption.md): `activeKeyId`, `keys` con KEK base64 casuale 32 byte; non aggiungerlo a Git, al browser o a immagini Docker. In Docker locale il mount atteso è `docker/secrets/envelope.keyring.json` → `/run/secrets/omnia-envelope.keyring.json`. Poiché il container usa `USER $APP_UID`, il file deve essere **leggibile solo dall'UID del container**: creare il file con proprietà/permessi stretti (0400/0440) sul host; se non leggibile API fallisce. Non rendere pubblica la KEK con chmod 644. Azioni CI generano key ring test ephemeral dentro RUNNER_TEMP con permessi 0600. L'adapter JSON da file è soltanto un meccanismo per sviluppo; in produzione servono Key Vault/OpenBao con identità di workload, backup delle chiavi, rotation e audit.

## Strategia di migrazione e gap

1. Questa prima release NON modifica il contenuto dei record già esistenti. Avvio sicuro richiede key ring esterna e compatibilità con vecchi record: la linea di codebase nuova sa leggerli, ma non genera più plaintext.
2. La seconda fase di #138 deve censire i vecchi record, progettare un backfill a batch re-entrant/stop-safe/transactional senza logging di plaintext, con strumenti per rollback/restore e controlli sul numero di legacy `sealed_payload IS NULL`, poi introdurre un vincolo `sealed_payload NOT NULL`.
3. Devono essere valutati separatamente i `embedding vector(768)` (ricerca semantica su database con dati in chiaro vs impossibilità di usare il consueto indice ANN dopo cifratura), id sorgente, timestamp, consenso, backup/WAL, provider AI e recupero chiavi dopo disaster recovery.
4. La rotazione delle KEK persistite non è schedulata; `Rewrap` del codec #137 funziona ma non esiste ancora un batch/registro durabile per i payload delle note.

## Test

- `SealedMemoryTests.cs`: pgvector reale con ruolo runtime non privilegiato RLS, ciphertext non vuoto e assenza di copia plaintext dei quattro campi, idempotent retry stesso ID, owner isolation, roundtrip timeline/export, tamper fail-closed, erasure, blocco INSERT plaintext al livello PostgreSQL.
- E2E Keycloak e browser reale: login OIDC, consenso, note, export, cancellazione (verificato dal job separato).
- Controlli supplementari key file missing/bad tramite avvio API, e CI/Pages. Annotare risultati effettivi nell'issue #139.

## Fonti

- OWASP Cryptographic Storage: https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html
- OWASP Key Management: https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html
- PostgreSQL ALTER TABLE: https://www.postgresql.org/docs/17/sql-altertable.html


## #140 — Backfill offline per singolo attore (prima slice, non cutover globale)

Un'utility separata da Omnia.Api è disponibile in src/backend/Omnia.Backfill.
Non espone endpoint HTTP: accetta solo configurazione tramite environment e
secret mounts protetti, mai dal client browser. Il ruolo PostgreSQL opzionale
omnia_backfill è separato da API, migrazioni e retention, senza BYPASSRLS/
ownership, con SELECT/UPDATE sulla sola tabella memory_entries. L'utility
verifica FORCE RLS, seleziona un tenant e subject esplicitamente autorizzati,
acquisisce PostgresActorLock prima di FOR UPDATE SKIP LOCKED, elabora batch
fino a 500 righe e fa commit solo dopo sealing AES-GCM e redazione atomica.

### Setup manuale sicuro (Docker locale)

Nel file gitignored docker/.env generare una password casuale distinta per
BACKFILL_DB_PASSWORD, avviare db-bootstrap e poi impostare
OMNIA_BACKFILL_ROLE=omnia_backfill prima di avviare la API/migrator che
concede i privilegi minimali. NON inserire il valore della password in Git.
In staging e produzione provisionare il ruolo con DBA e secret vault;
non usare il login API o owner per l'utility.

Nel terminale amministrativo isolato esportare OMNIA_BACKFILL_DB come stringa
Npgsql dell'identità omnia_backfill, OMNIA_BACKFILL_KEYRING_PATH come percorso
assoluto alla keyring esistente (incluso vecchio key ID), OMNIA_BACKFILL_TENANT,
OMNIA_BACKFILL_SUBJECT, e facoltativamente OMNIA_BACKFILL_BATCH_SIZE (1–500,
default 100). Poi eseguire:

```sh
dotnet run --project src/backend/Omnia.Backfill/Omnia.Backfill.csproj -c Release
```

Output: solo numero di righe sigillate e rimanenti per l'attore.
Exit 0 = nessun legacy per quell'attore al momento della verifica;
exit 2 = esecuzione incompleta (limite 100 batch/concorrenza);
exit 1 = errore, con rollback del batch corrente e messaggio sanificato.
Rieseguire per ogni attore autorizzato con inventario esterno verificato.

La utility NON recupera note eliminate, non proroga TTL né ricrea consensi;
mantiene inalterati UUID, provenance, embedding e timestamp. L'atomicità
protegge da arresto tra cifra e redazione, ma non elimina il plaintext già
presente in WAL, backup e repliche precedenti.

**#140 resta aperta:** mancano censimento globale sicuro, prova globale
count(sealed_payload IS NULL)=0, failure-injection e race test erase/capture,
restore/backup, Azure managed PostgreSQL e cutover schema/fallback.
L'esistenza dell'utility non consente l'uso di dati reali.
Vedere [ADR 0012](../adr/0012-actor-scoped-offline-legacy-backfill.md).

## #140 — Contatore globale riservato (ADR 0013)

La nuova migrazione `0006_legacy_global_audit.sql` introduce un conteggio
**esatto** delle righe `sealed_payload IS NULL` in tutto il PostgreSQL
primario, incluse quelle scadute non ancora eliminate dalla retention.
La funzione non restituisce ID di utenti, tenant né contenuti. Non è un
endpoint API e non espone la lista di soggetti da migrare.

L'account `omnia_backfill_audit` deve essere provisionato separatamente
dalle identità API, schema owner, retention e backfill. La grant `EXECUTE`
è opt-in da `PostgresMigrator`; nessuna credenziale o chiave in Git.
Per Docker locale impostare password casuale in
`BACKFILL_AUDIT_DB_PASSWORD` dentro `docker/.env`, eseguire `db-bootstrap`,
quindi impostare `OMNIA_BACKFILL_AUDIT_ROLE=omnia_backfill_audit` prima
del migrator. In Azure il provisioning e la prova con owner non-superuser
devono essere verificati separatamente.

Con sola credenziale audit in `OMNIA_BACKFILL_AUDIT_DB` eseguire offline:

```sh
dotnet run --project src/backend/Omnia.BackfillAudit/Omnia.BackfillAudit.csproj -c Release
```

Exit code 0 = zero legacy nella fotografia transazionale del conteggio;
2 = righe legacy ancora presenti; 1 = configurazione/privilegi/DB errati.
L'utility non può leggere le righe né cifrarle. Non esportare conteggi
in log pubblici. Zero non autorizza automatic cutover; prima occorrono
inventario e piano di arresto scritture, restore drill, verifica backup/WAL,
recovery KEK e test su Azure. La SELECT policy aggiuntiva vale solo per
il proprietario di schema, che rimane un'identità DDL privilegiata.

Decisione: [ADR 0013](../adr/0013-global-legacy-count-audit.md).
## #140 — Inventario attori per backfill offline (ADR 0014)

La CLI `src/backend/Omnia.Backfill` supporta ora **due modalità alternative**.
La modalità per singolo attore conserva `OMNIA_BACKFILL_TENANT` e
`OMNIA_BACKFILL_SUBJECT`. La nuova modalità multi-attore richiede invece
`OMNIA_BACKFILL_INVENTORY_PATH`, percorso **assoluto** di un file JSON
locale approvato dall'operatore. Non impostare i selettori singoli quando
si utilizza l'inventario. Il file deve essere privato (Unix `chmod 600`
o `chmod 400`, no symlink) e NON va committato, allegato ad issue o
inserito negli artifact CI. Esempio esclusivamente sintetico:

```json
{
  "schemaVersion": 1,
  "operatorApproved": true,
  "actors": [
    { "tenantId": "tenant-sintetico", "ownerSubject": "alice-test" },
    { "tenantId": "tenant-sintetico", "ownerSubject": "bob-test" }
  ]
}
```

Questo flag è soltanto un'attestazione operativa **non verificata**; l'operatore
deve assicurare fuori banda la legittimità e completezza dell'inventario e
l'autorizzazione alla manutenzione. L'utility rifiuta manifest non conformi,
duplicati, proprietà impreviste, più di 1000 attori e file oltre 256 KiB.
La connessione `OMNIA_BACKFILL_DB` resta quella del ruolo dedicato
`omnia_backfill`, con key ring in `OMNIA_BACKFILL_KEYRING_PATH`.
Non usare le credenziali API, audit o schema-owner: il nuovo
`PostgresBackfillRoleGuard` rifiuta quei privilegi.

In ambiente isolato impostare la variabile `OMNIA_BACKFILL_INVENTORY_PATH`
e lanciare la stessa CLI offline documentata sopra. Il processo esegue per
ogni attore al massimo 100 batch (1–500 righe per batch), nel rispetto di
RLS e dei lock condivisi con ingest/erasure. Stampa solo le somme aggregate.
Exit 0 significa che **tutti gli attori elencati** non avevano più righe
legacy all'ultima verifica; exit 2 segnala attori con residue; exit 1
un errore, con rollback del batch corrente. Ripetere in sicurezza dopo
un arresto, mantenendo tutte le versioni KEK necessarie.

**Non** interpretare exit 0 come completamento globale del backfill:
occorre controllare separatamente `Omnia.BackfillAudit` con ruolo count-only,
completare restore drill, verificare backup, WAL, metadata e embeddings.
La utility non elenca automaticamente soggetti e non attiva il cutover.

Decisione: [ADR 0014](../adr/0014-approved-actor-manifest-backfill.md).



## #140 — PostgreSQL logical restore gate (ADR 0015)

The synthetic CI rehearsal uses pg_dump -Fc and pg_restore into a
second database in the same disposable PostgreSQL 17 cluster. It checks
canonical AEAD AAD, raw/normalized/summary/tags, RLS timeline/export,
original TTL/vector, key-version continuity, missing-old-key rejection
and scope-limited erasure. This is not a real backup recovery or
production readiness certificate. See
[docs/restore-rehearsal.md](restore-rehearsal.md).


## #109/#140 — Cross-cluster backup verification (ADR 0016)

The CI now additionally restores the same synthetic PostgreSQL dump
into a **separate pgvector container** (not merely another database),
after first demonstrating a missing-global-role restore failure.
Distinct PG system IDs and fresh passwords are asserted; the
non-owner recovered runtime role must enforce scoped access to both
personal tables. Existing memory encryption and historical KEK are
verified end-to-end after recovery. This still does NOT prove
production backup encryption, archived copies, Azure/PITR or cutover.
