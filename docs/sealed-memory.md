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
