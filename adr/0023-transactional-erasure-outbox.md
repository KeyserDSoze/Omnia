# ADR 0023 — Outbox transazionale PostgreSQL per eventi di cancellazione

**Data:** 2026-11-20  
**Stato:** Accepted, solo primo slice di #141/#31; NO publisher esterno, NO ledger anti-rollback di produzione.

## Problema e contesto

Le cancellazioni confermate dal JWT/tenant e dalle policy di Omnia
rimuovevano `consent_grants` e `memory_entries` nella stessa transazione
e sotto `PostgresActorLock`, ma non producevano alcun evento persistente
correlabile alla richiesta. Il journal locale firmato ADR 0022 è una
fixture CI, non uno scrittore transazionale del backend. Emissione
separata nel filesystem sarebbe vulnerabile a crash tra COMMIT DB e
append, lasciando una cancellazione non registrata oppure un evento
per una cancellazione mai confermata.

## Decisione

- Migrazione versionata `0007_erasure_outbox.sql`: tabella
  `public.erasure_outbox` nel **medesimo PostgreSQL primario** con
  `id` UUID unico, `tenant_id`/`owner_sub`, `event_type` ristretto
  a `erase-actor` e `created_at` UTC di origine server.
  Non contiene testo, embedding, contenuto del documento o segreti.
  Gli identificatori degli attori restano comunque dati personali
  e richiedono una policy specifica di minimizzazione, accesso,
  retention, esportazione ed erasure.
- La tabella ha `ENABLE ROW LEVEL SECURITY` e `FORCE ROW LEVEL
  SECURITY`. L'unica policy `FOR INSERT ... WITH CHECK` richiede
  match del tenant/subject nel contesto `SET LOCAL` di
  `PostgresActorScope` (JWT validato a monte).
- Solo `omnia_app` riceve `INSERT` sulle colonne esplicite
  `id,tenant_id,owner_sub,event_type`, senza privilegi `SELECT`,
  `UPDATE`, `DELETE`, `TRUNCATE` o `INSERT` su `created_at`.
  Né `omnia_backfill`, `omnia_backfill_audit` né `omnia_retention`
  ricevono accesso alla nuova tabella. Nessun endpoint pubblico
  permette di leggere o pubblicare la coda. L'owner delle
  migrazioni rimane il solo amministratore della tabella.
- `PostgresPersonalDataService.EraseAsync` acquisisce scope e actor
  lock, cancella i consensi, cancella le note, inserisce l'evento
  e **COMMIT una sola volta**. Se l'`INSERT` fallisce, la
  transazione PostgreSQL abortisce anche i due DELETE. È possibile
  registrare l'evento anche quando zero righe del primario erano
  presenti: copie storiche PITR, sistemi esterni o retry possono
  richiedere ugualmente una riconciliazione.
- Il controllo comune `PostgresRuntimeGuard.VerifyAsync` richiede
  non-superuser/non-owner e RLS forzata su TUTTE e tre le tabelle,
  senza capacità read/write sulla coda oltre l'eventuale append.
  `VerifyApiAsync` aggiunge la verifica dei quattro permessi
  `INSERT` di colonna e vieta modifiche a `created_at`; è invocato
  dal bootstrap API, mentre backfill e audit mantengono il
  controllo comune senza privilegi di pubblicazione.
- Test TDD PostgreSQL con password nonowner verificano evento
  creato con metadata minimali, isolamento tra tenant e ruoli,
  lettura negata al runtime, WITH CHECK RLS cross-actor,
  e rollback completo provocando il fallimento dell'outbox.
  Testano anche la richiesta ripetuta con zero righe cancellate.

## Tradeoff e limitazioni esplicite

**L'outbox risiede nel medesimo database ripristinato**: un PITR a
un punto precedente alla cancellazione fa sparire sia la
cancellazione sia il relativo evento. Questa soluzione risolve
soltanto l'atomicità locale, NON la recuperabilità degli eventi
dopo disaster recovery e NON il rollback del journal esterno.

Il ruolo runtime può generare eventi propri se compromesso; RLS e
l'origine JWT del servizio non sono firme di richieste GDPR. Nessun
publisher, replica/ACK, retry, DLQ, retention sicura, sequencing
globale, watermark indipendente, immutabilità contro il DB owner,
outbox scanner con lock/lease, Azure/OpenBao, policy di release o
test su produzione. Chiamate ripetute creano eventi nuovi con UUID
diversi: **non esiste ancora idempotency key per richiesta** e
non si presume consegna exactly-once.

**Attenzione ai diritti GDPR:** esportazione, cancellazione ed
inventory end-to-end non includono oggi la nuova tabella di
metadata di audit. Va definita una base giuridica, una
conservazione minima e come rispondere alle richieste dell'utente
senza cancellare impropriamente evidenze necessarie. Non dichiarare
completi gli artt. 15/17/20 né la produzione SaaS.

Restano OPEN #141, #31, #109, #125 e #33. Il prossimo slice deve
essere un publisher indipendente con autorizzazione operativa,
acknowledgement e checkpoint off-backup verificabili, non una
chiamata HTTP asincrona non transazionale nel percorso di DELETE.

## Fonti ufficiali

- PostgreSQL 17, transazioni atomiche: https://www.postgresql.org/docs/17/tutorial-transactions.html
- PostgreSQL 17, RLS e FORCE: https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- PostgreSQL 17, CREATE POLICY e WITH CHECK: https://www.postgresql.org/docs/17/sql-createpolicy.html
- GDPR, Regolamento (UE) 2016/679: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/