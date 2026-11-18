# ADR 0012 — Backfill offline incrementale delle note legacy per attore

**Data:** 2026-11-18  
**Stato:** Accepted per una prima slice di #140, NON per il cutover globale.

## Contesto

La fase #139 cifra ogni nuovo record di memoria, ma le vecchie righe
pre-0005 hanno sealed_payload NULL e i quattro campi persistiti in chiaro.
Usare pgcrypto SQL con KEK inviata al DB o aggiungere un endpoint HTTP di
migrazione creerebbe rischi evitabili.

## Decisione

L'eseguibile .NET 10 Omnia.Backfill opera solo offline. L'operatore seleziona
un singolo tenant e soggetto dall'inventario autorizzato, mai da richieste HTTP.
Il ruolo separato omnia_backfill ha SELECT/UPDATE su memory_entries, senza
INSERT/DELETE, senza proprietà di tabella, SUPERUSER o BYPASSRLS, e con FORCE
RLS e scope locale a transazione. Il provisionamento è esplicito e opt-in;
nessuna chiave o password viene creata dal codice.

Il servizio prende prima il lock advisory dell'attore, condiviso con capture,
export ed erase; solo in seguito ottiene il lock di riga con FOR UPDATE SKIP
LOCKED e limite 1–500. Nella stessa transazione sigilla usando envelope
AES-256-GCM con AAD tenant/soggetto/UUID originale e redige raw, normalized,
summary e tags. Una failure provoca rollback del batch. Non modifica ID,
received_at, expires_at, occurred_at, embedding, origine né consensi; una
cancellazione concorrente non viene vanificata e non si crea alcuna nuova riga.

Le credenziali DB e il key ring sono esterni all'applicazione e non sono
stampati in log. L'utility mostra solo conteggi per chiamata, non identifier
di account o contenuto. La lettura legacy resta permessa fino al cutover.

## Conseguenze, alternative e test mancanti

Il contatore e il completamento sono solo **per attore**: non è ancora un
censimento globale. Servono inventario autorizzato, count globale zero,
failure injection e concorrenza erasure verificata, idempotenza su restart,
restore drill e prove su Azure Flexible Server prima di aggiungere
sealed_payload NOT NULL o eliminare la lettura legacy. Il ruolo maintenance
legge ancora i dati in chiaro finché esistono: isolare il processo, limitarne
la durata e applicare audit senza PII.

La cifratura **non copre** embeddings pgvector, metadati, WAL/backup, provider
AI, altri storage e token. Restano aperte #140, #138, #33 e i gate privacy;
questo ADR non costituisce attestazione di conformità GDPR né readiness.

## Riferimenti

- PostgreSQL 17 SELECT / SKIP LOCKED: https://www.postgresql.org/docs/17/sql-select.html
- PostgreSQL 17 transaction locks: https://www.postgresql.org/docs/17/explicit-locking.html
- GDPR articoli 5 e 17: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
