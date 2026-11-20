# ADR 0035 — Riconciliazione read-only delle evidenze erasure senza falso commit proof

**Data:** 2026-11-20  
**Stato:** Accepted per safety test sintetici CI, non un protocollo DR production-ready.  
**Issue:** #141 (P0), #109, #31, #32.

## Contesto

ADR 0033 e ADR 0034 introducono un pre-intent con request UUID
sul secondo cluster PostgreSQL, poi usano lo stesso UUID
nella transazione erase+outbox del primario. La ricevuta
post-commit del witness con lo stesso UUID può arrivare dopo
il commit, non atomicamente. Dopo PITR del primario il record
outbox può sparire mentre l'intent sopravvive.

Serviva un componente che distinguesse con chiarezza
**ciò che è ancora visibile** da **ciò che è successo
storicamente**, senza promuovere l'intento a prova di commit
né attivare una cancellazione automatica.

## Decisione

1. `PostgresCorrelatedErasureReconciliation.InspectAsync(uuid)`
   è un audit soltanto in GitHub Actions CI, non un
   endpoint REST o un'autorizzazione a modificare dati.
   Ogni chiamata verifica separatamente tre SQL logins
   con soli privilegi EXECUTE su funzioni di lettura:
   `omnia_witness_intent_reader` (PRE-intent),
   `omnia_dispatcher` (outbox primary) e
   `omnia_witness_reader` (post-commit receipt).
   Ruoli writer/admin/app/owner/BYPASSRLS non validi.
2. Leggere pre-intent del secondo PGDATA e richiedere
   UUID non nullo, sequence positiva, tipo
   `erase-actor-intent` e scope tenant/owner valido.
   Se l'intent è assente o malformato, **NEGARE**
   anziché dire che non esiste richiesta.
3. Leggere source outbox solo tramite
   `public.omnia_read_erasure_outbox` in una
   transazione PostgreSQL `REPEATABLE READ` /
   `SET TRANSACTION READ ONLY`, con cursor temporaneo
   timestamp+UUID e massimo 1000 eventi, eseguendo
   un'ulteriore pagina di prova per rilevare overflow.
   La scansione è coerente all'interno del primario,
   ma NON costituisce ordine dei COMMIT.
4. Cercare post-commit witness receipt nel secondo
   cluster e confrontare UUID, tenant, owner, tipo
   `erase-actor` e sequence positiva con l'intent.
   Qualunque divergenza o login non autorizzato
   causa eccezione e non restituisce uno stato valido.
   I registri NON condividono uno snapshot distribuito
   quindi la classificazione è osservazionale.
5. Quattro risultati possibili, nessuno è un
   permesso di recovery:
   - `IntentOnlyCommitUnknown`: richiesta remota
     presente, né source outbox né ACK visibili.
     Non distingue MAI erase non iniziato,
     fallito, committato e poi perso da PITR.
   - `SourceEventPendingWitness`: source outbox
     visibile, receipt remota non visibile.
   - `SourceAndWitnessVisible`: entrambi i
     registri mostrano lo stesso evento, non
     garantisce completamento in cache/provider/backup.
   - `WitnessVisibleWithoutSource`: receipt
     witness visibile ma source outbox assente,
     compatibile con PITR o receipt forgiata.
   **`MayReleaseRecovery=false`** e
   **`MayAutoReplay=false`** in TUTTI gli stati.
6. TDD: test reali PostgreSQL usano un unico actor
   sintetico (pre-intent presente, poi DELETE +
   outbox source, poi publisher witness), verificano
   i tre stati temporali senza mai concedere release.
   Test puri attestano il quarto stato e i mismatch
   di UUID, tenant, subject e tipo senza avvelenare
   la history del witness condiviso.
   Il rehearsal fisico WAL/PITR del PRIMARIO
   usa il medesimo inspector prima e dopo rollback
   e richiede `SourceEventPendingWitness` prima,
   `IntentOnlyCommitUnknown` dopo, API sempre
   quarantinata e mai `ready`.

## Rischi e decisioni NON prese

Un osservatore vede solo snapshot del tempo corrente;
non è testimone di ciò che è stato committato in passato.
Pre-intent e receipt possono essere nel medesimo witness
PGDATA e possono entrambi retrocedere dopo un PITR di
quell'istanza. Anche quando il source e il witness
sono entrambi leggibili non esiste consenso transazionale
tra i due database, completamento della cancellazione
in ogni provider, né certificato indipendente di commit.

Non esistono ancora identità firmataria del richiedente,
tracciamento completo di ogni commit source prima del
primo ACK, custodia offsite immutabile/WORM, chiavi
vault/HSM, terzo-cluster anchor per la sequenza intent,
fencing globale di ingress/API/workers/repliche,
workflow GDPR comprovato o unità di release DR
autorizzata. #141/#109/#31/#32/#33/#125 rimangono
aperti. Nessun dato personale reale deve essere
processato tramite il prototipo.

## Fonti ufficiali

- PostgreSQL 17, SET TRANSACTION / snapshot read-only:
  https://www.postgresql.org/docs/17/sql-set-transaction.html
- PostgreSQL 17, consistenza a livello applicazione:
  https://www.postgresql.org/docs/17/applevel-consistency.html
- PostgreSQL 17, sicurezza funzioni:
  https://www.postgresql.org/docs/17/perm-functions.html
- GDPR articoli 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
