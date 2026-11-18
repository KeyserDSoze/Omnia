# ADR 0009 — Note PostgreSQL a scadenza e retention worker con ruolo SQL separato

**Data:** 2026-11-18  
**Stato:** accepted for the narrow #136 slice of #32; not a GDPR readiness sign-off

## Contesto

Il database `memory_entries` conserva testo originale, normalizzato e embedding senza scadenza. `consent_grants` ha una scadenza del consenso, che **non** elimina automaticamente dati storici. La cancellazione manuale #135 opera solo su richiesta; serve anche un limite automatico contro la conservazione indefinita. GDPR art. 5(1)(e) prescrive limiti proporzionati allo scopo, non una durata universale predefinita.

## Decisione

1. Scegliere una policy **provvisoria** di 30 giorni per le sole note nel PostgreSQL primario, misurata dall'originale `received_at` server. La migrazione 0004 imposta `expires_at`, backfill legacy da received_at, default server su nuove note e indice di scadenza. Il retry idempotente prima della scadenza non estende la durata, una nuova cattura intenzionale dopo la scadenza richiede il consenso e riavvia il TTL.
2. Escludere le righe scadute dal listing dell'API, indipendentemente dalla tempestività del purge. Mantenerle visibili nell'export completo *finché realmente conservate* e mostrarne `expiresAt`.
3. Per cancellare globalmente su tutti i tenant **senza assegnare BYPASSRLS all'API**: funzione DB `public.omnia_purge_expired_memories(batch_size)`, SECURITY DEFINER di schema-owner con SQL statico, `FOR UPDATE SKIP LOCKED`, batch 1..500, `search_path=pg_catalog,pg_temp`, EXECUTE revocato a PUBLIC e concesso solo a `omnia_retention`. Worker role LOGIN separato, senza direct SELECT/DELETE, proprietà di tabelle, superuser o bypass. Fail startup se non sicuro.
4. `RetentionSweepService` periodico in API: sweep iniziale e ogni 15 minuti, massimo 10 batch/ciclo; logging di sole metriche aggregate/tipo errore. Accesso worker tramite suo datasource separato. Gli altri percorsi usano `omnia_app` con FORCE RLS e `PostgresActorScope`. Ambienti Docker, CI e OIDC end-to-end creano tre account/secret distinti.

## Conseguenze / rischi residui

La funzione SECURITY DEFINER delega un'operazione molto specifica a uno schema-owner privilegiato; rivedere e testare ogni modifica della funzione per prevenire SQL injection o escalation. Worker e credenziali amministrative convivono oggi nello stesso processo di sviluppo, non su workload isolati; in produzione va separato il job, l'accesso al schema owner e il secret vault. `SKIP LOCKED` e iterazione a timer non equivalgono a schedulazione garantita/SLO, né purge immediato alla scadenza. Le richieste GDPR, il diritto di cancellazione, i backup e i diritti di terzi richiedono ulteriori procedure. I `consent_grants` hanno una finalità di audit diversa e richiedono una loro policy separata. Il parent #32 resta aperto per policy per tenant/category/purpose, i provider esterni e valutazioni legali.

## Evidenze previste

TDD red/green in xUnit con PostgreSQL reale, permessi worker, scadenze legacy, batch/idempotency e contesti distinti; GitHub Actions .NET, Expo e Keycloak browser; link SHA nelle issue #136 e #32.
