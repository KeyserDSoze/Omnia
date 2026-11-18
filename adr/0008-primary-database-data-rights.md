# ADR 0008 — Export streaming e cancellazione atomica del PostgreSQL primario

**Data:** 2026-11-18  
**Stato:** accettata per slice #31, non è certificazione GDPR né completamento dell'epic.

## Problema

La foundation ha `memory_entries` (raw, normalized, summaries, embedding), `consent_grants`, JWT OIDC, RLS. Manca una funzione che consenta all'utente di esportare i dati e rimuoverli dal DB; la semplice revoca di consenso blocca solo nuove note.

## Decisione

Definire port `IPersonalDataService` in Application con `WriteExportAsync` e `EraseAsync`. Infrastructure implementa entrambi con `PostgresActorScope` (transazione + RLS), con advisory `PostgresActorLock` per tenant+subject. Estendere Grant, Revoke, Upsert per acquisire **prima** lo stesso lock; erasure e scrittura in volo si serializzano, prevenendo la ricreazione di note dopo il commit dell'erasure. Esportazione server-side JSON tramite `Utf8JsonWriter` direttamente verso stream, includendo raw, normalized, metadata, consent history, embedding e vector model, senza limiti artificiali o query amministrative. Cancellazione: prima consensi, poi memorie in transazione unica, con conteggi di righe realmente eliminate. Nessun tombstone nel DB primario di questa prima slice; rilasciare receipt con scope chiaramente limitato e non conservare un documento contenente il dato cancellato.

API JWT protette: `GET /api/v1/privacy/export` e `POST /api/v1/privacy/erase` con conferma esatta. UI Web disponibile solo dopo OIDC, checkbox + testo `ELIMINA` prima dell'operazione; nessuna cancellazione implicita per cambio consenso. No caches HTTP su export. Native + Azure da verificare separatamente.

## Trade-off e rischio residuo

Il lock riduce le race tra operazioni della corrente API, ma NON blocca futuri job/connector che non usano lo stesso protocollo, né impedisce nuovi grants volontari dopo l'erasure. La risposta 200 si riferisce **solo al PostgreSQL primario**, non backup, WAL, device, external AI, Keycloak o eventuali store futuri. Il browser scarica un Blob completo in RAM; per dataset grandi servirà un percorso di download streaming protetto. La cancellazione unificata non sostituisce un processo DSAR con valutazione delle basi giuridiche, dei terzi coinvolti e dei termini di conservazione. Non dichiarare GDPR compliance solo per l'esistenza di API. Test reali con PostgreSQL e Keycloak e issue #31 restano il gate.
