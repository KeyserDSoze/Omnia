# ADR 0031 — Audit di consegna live dall'outbox del primario al witness

**Data:** 2026-11-20
**Stato:** Accepted per sola diagnosi e test di sicurezza sintetici CI (#141/#109/#31); NON prova di completezza storica o release DR.

## Problema

Le ADR 0023–0027 dispongono l'outbox erasure transazionale e la
pubblicazione idempotente verso il witness PostgreSQL separato.
Le ADR 0028–0030 provano il rollback del witness contro una terza
PGDATA di checkpoint. Manca un controllo operativo di **quali
eventi cancellazione già committati e ancora visibili nel primario
hanno ottenuto la stessa ricevuta dal witness**. Non basta un ciclo
di publisher completato per attestare tutte le consegne.

## Decisione

1. `PostgresErasureLiveDeliveryAudit` è un verificatore READ-ONLY
   del primario ANCORA LIVE, non del database PITR restaurato.
   Autentica due identità SQL distinte, EXECUTE-only:
   `omnia_dispatcher` (source) e `omnia_witness_reader` (witness),
   rifiutando admin, runtime app, writer o privilegi diretti.
2. Il primario viene letto in una sola transazione PostgreSQL
   `REPEATABLE READ` + `SET TRANSACTION READ ONLY`;
   il solo accesso consentito è
   `public.omnia_read_erasure_outbox(integer,timestamptz,uuid)`.
   Il paging usa created_at+UUID come cursore TEMPORANEO nella
   transazione, mai salvato come ordine di commit/ACK. Gli eventi
   devono avere UUID unico, attore valido e tipo `erase-actor`.
   La scansione ha bound 1000 con un'ulteriore lettura da 1 per
   negare in caso di overflow: mai affermare completezza da un
   prefisso troncato.
3. `VerifyKnownEventAsync(uuid)` verifica che una specifica UUID
   esista nel committato snapshot live e abbia una ricevuta
   witness della medesima UUID, tenant, subject, event type e
   sequence positiva. Eventi sconosciuti, ricevute mancanti o
   con scope divergente sono NEGATI. `VerifyAllAsync` applica
   gli stessi controlli a TUTTI gli eventi sorgente entro il bound.
   Nessuna funzione cancella, ACKa, modifica il cursore persistito
   (inesistente) o imposta l'API `ready`.
4. `Omnia.ErasureDeliveryAudit --all` è una CLI separata
   dall'API, abilitata SOLO in GitHub Actions CI con opt-in esplicito,
   login `omnia_dispatcher` loopback:15432 e
   `omnia_witness_reader` loopback:15436. Nessun secret,
   actor o SQL error è emesso nei log. Dopo il publisher CI
   il comando verifica la consegna corrente, con smoke negativi
   su identità errate e flag mancante.
5. TDD RED: `LiveErasureDeliveryAuditTests` esercita davvero
   `EraseAsync`, comprova l'assenza dell'ACK witness prima
   della pubblicazione, poi invoca il publisher e conferma
   la stessa sequence su due auditor separati. Una prova
   di mismatch UUID/tenant/soggetto/tipo/sequence è solo in
   memoria per non creare ricevute false permanenti sul witness CI.

## Modello di fiducia e limiti

Un esito positivo prova esclusivamente che gli eventi già committati
e visibili nel snapshot del primario LIVE al momento dell'audit
avevano ricevute witness corrispondenti al momento del controllo.
Il primario può ricevere nuovi commit subito dopo lo snapshot.
**NON** è una prova di completezza di tutti i commit storici e
**NON** copre l'intervallo fra `COMMIT EraseAsync` e primo
witness ACK se il primario subisce un crash/PITR: il rollback può
eliminare proprio l'outbox da cui l'audit dovrebbe partire.
Non esiste qui una sorgente indipendente monotona dei commit,
external WAL/CDC attestation, custodia offsite WORM/HSM,
release gate globale con fencing di API/worker/repliche,
cancellazione provider/cache/media/device/backup né
analisi GDPR documentata del ciclo di vita dei receipt.
Il gate è diagnostico e non viene esposto all'API pubblica.

**Restano OPEN #141, #109, #31, #32, #33 e #125.**

## Riferimenti ufficiali

- PostgreSQL 17, transazioni read-only/snapshot:
  https://www.postgresql.org/docs/17/sql-set-transaction.html
- PostgreSQL 17, consistency check di applicazione:
  https://www.postgresql.org/docs/17/applevel-consistency.html
- PostgreSQL 17, funzioni SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- GDPR artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
