# Tao — conservazione dati e pulizia automatica (prima slice #136)

## Stato e criterio legale

**Implementazione attuale:** solo `public.memory_entries` nel database PostgreSQL primario, inclusi tutti i suoi derivati inline e embedding. È stata scelta in modo **provvisorio e uniforme una finestra di 30 giorni da `received_at`**, data imposta dal server. Non è una durata richiesta dal GDPR. **GDPR art. 5(1)(e)** chiede di non conservare più del necessario per la specifica finalità, con eccezioni da verificare; il titolare deve giustificare scadenza e finalità, considerare vincoli legali e documentare i diritti dei terzi. Fonti UE: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/ ; https://commission.europa.eu/law/law-topic/data-protection/information-business-and-organisations/principles-gdpr_en

## Semantica delle note

- La migrazione `0004_memory_retention.sql` aggiunge `expires_at timestamptz NOT NULL DEFAULT(now()+interval '30 days')` e fa il backfill delle righe preesistenti da `received_at + interval '30 days'` (non dal tempo di migrazione).
- L'istante `occurred_at` è un fatto storico e **non** decide quando cancellare. Un retry idempotente del medesimo `(tenant, owner, source, external_id)` durante il TTL non lo estende. Una cattura **esplicita** del medesimo ID *dopo* la scadenza reimposta il TTL dopo nuova verifica del consenso.
- `GET /api/v1/memories` non mostra note scadute anche quando un worker è inattivo o la riga è in attesa di purge. L'export JSON, per completezza dell'accesso ai dati detenuti, include tutte le righe ancora esistenti con `expiresAt`, compresi eventuali scaduti in attesa del worker.

## Worker: permessi e periodicità

`Database:RetentionConnectionString` è obbligatorio e utilizza `tao_retention`, ruolo **distinto da `tao_app` e dall'amministratore di migrazione**, senza accesso diretto a note/consensi, senza privilegi SUPERUSER, BYPASSRLS o ownership. Il solo grant DB è EXECUTE su `public.tao_purge_expired_memories(integer)`, funzione `SECURITY DEFINER` con corpo SQL fisso, `search_path` trusted, limiti 1..500 e locking `FOR UPDATE SKIP LOCKED`. Un controllo automatico allo startup rifiuta ruoli non conformi. La funzione non è invocabile tramite connessione API.

`RetentionSweepService` viene avviato nel processo API, esegue un sweep immediato poi ogni 15 minuti. Il ciclo cancella fino a 5.000 righe per esecuzione (batch 500), limitando l'impatto sui tenant; due istanze possono lavorare in parallelo. Nel log compaiono **solo numeri aggregati**, non note, identità o query. Dopo errori il ciclo successivo ritenta. Il worker non ha una garanzia di esecuzione se l'API è spenta e non è ancora un job autonomo durevole.

### Configurazione

- Docker: configurare tre password diverse in `docker/.env`: `POSTGRES_PASSWORD`, `APP_DB_PASSWORD`, `RETENTION_DB_PASSWORD`; `db-bootstrap` crea i ruoli non privilegiati anche per volumi precedenti, `api` esegue la migrazione e il worker.
- CI: PostgreSQL pgvector usa credenziali random distinte e testa il ruolo dedicato, le policy RLS, il TTL, i batch, un altro utente dello stesso tenant, un altro tenant e la rimozione degli embeddings.
- Azure: la stessa migrazione e SQL PostgreSQL sono portabili, ma devono essere verificati su Flexible Server; servono provisioning sicuro del ruolo, managed secret/key vault e worker isolato dalla API.

## Limiti, rischi e gap di #32

Il worker invoca una funzione SECURITY DEFINER proprietaria di uno schema role privilegiato; modificarne il codice richiede controllo delle migrazioni e code review. Il ruolo tecnico senza permessi non può farne il bypass, ma la funzione *può* cancellare righe scadute di qualsiasi tenant: questo è il suo compito limitato. Da tenere separato dal contesto HTTP.

NON sono ancora implementati: policy per fonte, finalità, categoria, età, tenant o eccezioni legali; eliminazione di `consent_grants` (storico per finalità diversa), backup/WAL e restore-after-erasure, AI provider, log, audio su device, futuri store esterni. Serve una valutazione giuridica per i 30 giorni e un inventario di ogni storage, nonché metriche e alert sull'arretrato. Il GDPR non prescrive un periodo unico universale, quindi non presentare questa scelta come conformità.

## Test di accettazione

`tests/Tao.Integration.Tests/RetentionTests.cs` con PostgreSQL reale, ruolo owner/test-fixture, app RLS e retention-only; `.github/workflows/ci.yml` esegue xUnit. `.github/workflows/oidc-e2e.yml` continua a verificare il browser Keycloak→API con worker avviato. Documentare URL e commit verdi nella sotto-issue #136; il parent #32 rimane aperto.


## Retention note sealed (#139)

La funzione di purge esistente cancella l'intera riga, incluso `sealed_payload`; nessuna seconda coda di cancellazione per il payload è necessaria nel PostgreSQL primario. Il TTL di 30 giorni decorre sempre da `received_at`, non dalla data di re-encryption, e un retry di una nota viva non deve alterarlo. Embedding pgvector, metadata e note storiche non cifrate rimangono rischi privacy autonomi.
