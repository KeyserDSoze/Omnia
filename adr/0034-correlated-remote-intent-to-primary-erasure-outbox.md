# ADR 0034 — Correlazione fail-closed tra pre-intent e outbox DELETE (solo CI)

**Data:** 2026-11-20  
**Stato:** Accepted, fetta di sicurezza sintetica/testata su PostgreSQL 17 per #141; non attivata nell'API e non production-ready.

## Problema

ADR 0033 ha introdotto un pre-intent remote sul witness
prima di una cancellazione nel primario, ma il normale
`PostgresPersonalDataService.EraseAsync` crea un UUID
nuovo nella propria `erasure_outbox`, distinto da quello
dell'intent. Non era possibile correlare deterministicamente
la richiesta al commit senza conoscere l'identità/ambito
dell'attore e consultare altre sorgenti. Un pre-intent
orfano non può comunque dimostrare il successo del DELETE.

## Decisione

1. Aggiungere un metodo ESPLICITO
   `EraseWithWitnessedIntentAsync(requestId,tenant,owner,
   intentReader,ct)` confinato a `CI=true` /
   `GITHUB_ACTIONS=true`. NON è registrato né invocato
   dall'API REST pubblica: `IPersonalDataService.EraseAsync`
   mantiene il contratto e il comportamento esistenti.
2. Prima di qualunque transazione sul primario,
   `PostgresPreEraseIntentReader.FindAsync` deve autenticare
   il ruolo SQL EXECUTE-only sulla SECONDA PGDATA,
   leggere l'intento con `requestId` non vuoto
   e richiedere identici tenant, owner e operazione
   `erase-actor-intent`. Se assente, off-scope,
   ruolo sbagliato o PG witness indisponibile, negare
   il DELETE senza operazioni sul source.
3. Utilizzare `requestId` come `public.erasure_outbox.id`
   nell'unica transazione RLS/actor-lock che cancella
   consensi e memorie. Il tipo evento resta
   `erase-actor`. Il publisher esistente trasmette
   lo stesso UUID in `witness.erasure_events.event_id`,
   MA solo quando la transazione primary è ancora visibile.
4. Sul retry con lo stesso UUID, la PK dell'outbox
   lancia `unique_violation 23505`. L'intera transazione
   seconda deve rollbackare, inclusi i DELETE eseguiti
   prima dell'INSERT; non usare `ON CONFLICT DO NOTHING`
   e NON rimuovere dati nuovi acquisiti dopo il
   primo erase. Un risultato di COMMIT incerto
   richiede verifica/recovery fuori banda, non retry
   automatici indiscriminati.
5. TDD: una prova d'integrazione con vero PostgreSQL
   crea dati sintetici e dimostra i rifiuti di
   intent assente, SQL reader non autorizzato,
   UUID fuori ambito; verifica gli stessi UUID
   dopo erasure in outbox primary e ricevuta
   witness, e la conservazione di dati/consenso
   successivi in un tentativo di replay duplicato.
   Il test fisico `pg_basebackup`/WAL PITR ADR 0032
   usa adesso la richiesta UUID dell'intento
   anche nel COMMIT del primario, poi documenta
   che l'outbox e il DELETE possono comunque
   scomparire prima del primo ACK remote.

## Sicurezza e confini

La correlazione UUID è una prova sintetica di
identità *del record*, NON una firma di chi ha
richiesto la cancellazione e NON una prova del
COMMIT dopo il disastro. Il witness pre-intent può
esistere senza successo primary, o retrocedere con
PITR del witness; solo la receipt POST-commit
conferma la consegna, non il commit non osservato.
Il test non cambia l'autenticazione OIDC, non rende
atomico un protocollo tra due database, non
autorizza cancellazioni in recovery e non emette
`ready`. La release di produzione richiede
OIDC origin actor, consenso/autorizzazione GDPR,
attestazione di commit indipendente e monotona,
firma/hardware vault/offsite WORM, fencing globale
ingress/API/worker/repliche, raw media/provider,
e policy retention per i metadati. Queste misure
**restano OPEN** su #141, #109, #31, #32, #33, #125.

## Fonti autorevoli

- PostgreSQL 17, transazioni e rollback:
  https://www.postgresql.org/docs/17/tutorial-transactions.html
- PostgreSQL 17, controlli di unicità:
  https://www.postgresql.org/docs/17/index-unique-checks.html
- PostgreSQL 17, gestire errori di serializzazione/ri-tentativi:
  https://www.postgresql.org/docs/17/mvcc-serialization-failure-handling.html
- PostgreSQL 17, SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- GDPR, art. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/

**Limite fondamentale del retry:** la PK UUID impedisce il riuso
soltanto quando l'outbox originale è ancora presente nel primario.
Se un PITR ne elimina la riga, la protezione locale non può
riconoscere il precedente commit; la request non deve essere
riprodotta automaticamente sul database recuperato.
Il sistema deve rimanere in quarantena fino a un'audit
indipendente e un'autorizzazione di recovery completa.
