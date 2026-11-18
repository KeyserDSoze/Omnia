# ADR 0013 — Verifica globale del backfill con ruolo audit count-only

**Data:** 2026-11-18  
**Stato:** Accepted per verifica read-only di #140, non per cutover.

## Problema

Il backfill #140 e il relativo contatore sono delimitati da tenant e subject via
RLS. Un risultato zero per attore non dimostra che tutto il PostgreSQL primario
abbia terminato il backfill. Un conteggio globale attraverso la credenziale
HTTP o un dump con ID sarebbe inappropriato. FORCE RLS si applica anche
all'owner non-superuser e non si può presumere BYPASSRLS su Azure.

## Scelta

- Migrazione SQL 0006 crea una funzione a firma fissa
  `public.omnia_count_legacy_memories()` con `SECURITY DEFINER`, search_path
  vincolato a `pg_catalog, pg_temp`, SQL statico e solo ritorno `bigint`.
  `REVOKE EXECUTE FROM PUBLIC` avviene nella stessa transazione di CREATE.
- Solo il migration/schema owner (già autorizzato alle modifiche DDL)
  riceve una SELECT policy RLS mirata. Questo permette al function owner
  non-superuser di contare tutte le righe senza assegnare BYPASSRLS.
  L'owner resta un'identità privilegiata: mai riutilizzarlo nella API.
- Un ruolo opzionale indipendente, `omnia_backfill_audit`, non-owner,
  non-superuser e privo di privilegi sulle tabelle personali, ottiene soltanto
  USAGE schema + EXECUTE sulla funzione. Il migrator rifiuta un ruolo
  audit con privilegi di tabella o membership nell'owner.
- `PostgresLegacyBackfillAudit` verifica RLS e assenza di privilegi diretti,
  poi invoca il conteggio. L'eseguibile separato `Omnia.BackfillAudit`
  usa una credenziale esterna, stampa solo il numero globale e restituisce
  exit 2 se non è zero, exit 1 su errori e exit 0 se è zero.
- Il controllo è puntuale e **non** blocca scritture concorrenti o nuovi
  cicli di retention; non produce una prova di cutover durevole.

## Limiti e gate ancora aperti

Non serializza un cutover, non sostituisce un inventario autorizzato degli
attori con dati legacy, non esegue il backfill, non elimina né modifica dati
e non installa un vincolo `sealed_payload NOT NULL`. Anche in caso di zero
legacy restano restore drill, WAL/backup, vector, metadata, vault, Azure
staging, chiavi di recovery e privacy/legal review. Il numero globale
è un dato operativo riservato e deve restare confinato all'auditor offline.

## Riferimenti ufficiali

- https://www.postgresql.org/docs/17/sql-createfunction.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- https://www.postgresql.org/docs/17/sql-revoke.html
- https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/