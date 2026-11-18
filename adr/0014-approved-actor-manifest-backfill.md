# ADR 0014 — Inventario esplicito e backfill offline multi-attore con ripresa

**Data:** 2026-11-18  
**Stato:** Accepted per slice incrementale #140, non per cutover globale/produzione.

## Contesto

Il worker #140 migra un singolo attore alla volta, con RLS ed actor-lock. Il
contatore globale #140/ADR 0013 mostra quante righe legacy mancano ma non
rivela i soggetti, giustamente. Per completare un rollout amministrativo
serve un inventario di attori verificato, distinto dal DB audit e non
derivato da headers/API pubbliche, senza esporre account nei log.

## Decisione

- Manteniamo separati i processi `Omnia.Backfill` (SELECT/UPDATE RLS per
  attore) e `Omnia.BackfillAudit` (solo conteggio globale read-only).
- L'operatore configura **in alternativa** la coppia di variabili
  `OMNIA_BACKFILL_TENANT` + `OMNIA_BACKFILL_SUBJECT` oppure
  `OMNIA_BACKFILL_INVENTORY_PATH` (percorso assoluto al file JSON privato).
  Sono proibiti entrambi i selettori contemporaneamente.
- JSON versione 1 con solo `schemaVersion: 1`, `operatorApproved: true` e
  `actors: [{tenantId, ownerSubject}]`. L'approvazione è una conferma
  operativa del responsabile, NON un consenso GDPR e NON sostituisce
  verifica degli attori e delle fonti autorizzate. Rifiutiamo proprietà
  extra/duplicate, soggetti vuoti, spazi aggiunti, duplicati e liste
  oltre 1000 elementi. Dimensione massima 256 KiB, no symlink, permessi
  Unix stretti 0400/0600 (nessuna permission group/other).
- `LegacyBackfillRunner` chiama il worker esistente per ciascun attore
  con al massimo 100 batch da 1–500 righe, mantenendo l'ordine lock
  attore → righe FOR UPDATE SKIP LOCKED → UPDATE atomico e commit.
  Interruzioni causano rollback **solo del batch corrente**; batch
  precedenti persistono e si può rieseguire senza prorogare TTL.
- L'eseguibile registra soltanto conteggi aggregati di righe sealed e
  attori con righe residue, exit 0/2/1; non stampa identità, key ID,
  payload, percorsi di file o dettagli delle eccezioni.
- Un guard PostgreSQL specifico per CLI ammette soltanto credenziali
  non-owner con SELECT/UPDATE su `memory_entries`, senza INSERT/DELETE,
  senza diritti `consent_grants` o funzione audit globale; respinge
  l'identità API e quella dell'audit.

## Trade-off e limiti

L'inventario non viene scoperto da Omnia e rimane materiale sensibile
fornito fuori banda da un operatore con autorizzazione documentata. Un
file arbitrario con `operatorApproved=true` non prova alcun diritto;
il suo approvvigionamento, custodia e cancellazione richiedono processo
organizzativo. I risultati sono per manifest e consistenti solo alla
fine di ogni singola query. Non costituiscono una prova di completezza
globale e non attivano automatic cutover. Se una voce resta incompleta
la utility esce con codice 2; un operatore deve riprendere l'operazione.

#140 resta OPEN: inventario completo degli attori reali, restore drill,
blocco del rollout concorrente, Azure PG non-superuser, gestione KEK
e backups/WAL, vettori e metadati, verifica globale zero, schema
`sealed_payload NOT NULL` e rimozione fallback non sono completati.

## Riferimenti

- [PostgreSQL 17 locking](https://www.postgresql.org/docs/17/explicit-locking.html)
- [PostgreSQL 17 SELECT / SKIP LOCKED](https://www.postgresql.org/docs/17/sql-select.html)
- [OWASP Secrets Management](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html)
- [OWASP Logging](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html)