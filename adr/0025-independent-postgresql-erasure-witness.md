# ADR 0025 — Witness PostgreSQL separato dal primario per eventi di cancellazione

**Data:** 2026-11-20
**Stato:** Accepted per prototipo sintetico su due cluster PG17 (#141, #109); non production-ready.

## Motivazione

ADR 0023 garantisce commit atomico della cancellazione e della riga erasure_outbox sul primario. ADR 0024 fornisce un dispatcher a privilegi minimi, ma il journal HMAC era solo locale. Il PITR del database sorgente può eliminare un evento non ancora pubblicato. Serve una prova di persistenza in un failure domain PostgreSQL distinto.

## Decisione implementata

- La CI avvia omnia-ci-erasure-witness, container PostgreSQL 17 separato dal primario omnia-ci-pg, con database omnia_witness, porta loopback 15436, nuovo PGDATA, password casuali e system_identifier DIFFERENTE, verificato con pg_control_system().
- La definizione CI-only tests/restore/independent-witness.sql crea schema witness, tabelle erasure_events e journal_head e tre funzioni SECURITY DEFINER con search_path = pg_catalog,pg_temp e tabelle qualificate.
- omnia_witness_writer è un login nonowner/non-superuser senza BYPASSRLS, con solo EXECUTE su witness.append_erasure; omnia_witness_reader ha solo EXECUTE su witness.find_erasure e witness.list_erasures (max 100 per chiamata). Non esiste accesso diretto a tabelle o privilegi per PUBLIC sulle funzioni.
- Il witness conserva event_id UUID dell'outbox, tenant_id, owner_sub, event_type e sequenza progressiva, non il contenuto di note o embedding. Un UUID duplicato con stesso attore e tipo restituisce la stessa sequenza; se viene associato a un altro attore genera errore SQL.
- L'append usa una riga singleton bloccata con SELECT FOR UPDATE, ricontrolla l'UUID sotto il lock, inserisce l'evento e incrementa la sequenza NELLA STESSA TRANSAZIONE. A differenza di nextval(), un append fallito non consuma una sequenza irrevocabile. Si ordina il commit nel witness, non quello degli erase sul primario.
- PostgresErasureWitness e PostgresErasureWitnessReader eseguono le sole funzioni autorizzate. Il bridge PostgresErasureOutboxWitnessPublisher è TEST-ONLY e non è registrato nell'API: verifica i due login, riscorre la sorgente a ogni invocazione (senza cursor ACK) e prova a consegnare eventi fino al limite 1000.
- Test PostgreSQL in CI: erase realmente committato da PostgresPersonalDataService, invio al witness, replay idempotente, confronto di cluster system_identifier distinti, cancellazione tramite amministratore SOLO di una riga outbox sintetica nel primario, successiva presenza della ricevuta nel secondo cluster; login reader/writer privi di SELECT/UPDATE/DELETE su tabelle; conflitto UUID respinto. Il test di rimozione sintetica NON è un PITR vero della medesima richiesta, anche se il PITR fisico precedente continua a essere verificato separatamente.

## Limiti aperti (vincolanti)

I due database sono su container diversi ma sullo STESSO host temporaneo di GitHub Actions, non su storage offsite/geo-indipendente. Il witness non ha backup continuo, WORM/Object Lock, disaster recovery testato, vault, encryption at rest verificata o reliche resilienti; l'owner o un restore del witness possono riportare indietro anche la sequence. Il suo progressivo NON è un checkpoint indipendente anti-rollback, non dimostra la completezza degli eventi generati nella sorgente e non contiene una firma di autorizzazione privacy.

La sorgente conserva l'outbox finché possibile, ma un PITR precedente alla **consegna al witness** perde sia la cancellazione sia l'outbox. Il bridge è test-only: mancano un publisher programmato, persistent ACK, retry/dedup distribuiti, lease, allarmi/lag, error handling, replay cross-store e gate globale delle repliche e dei worker. Il cursor timestamp + UUID non rappresenta ordine di commit e non è usato come watermark persistente.

I metadata tenant e subject sono dati personali e necessitano base giuridica, minimizzazione, retention, inventario/export/erasure specifici. Nessuna promozione automatica del marker ready, nessun rollout Azure/on-prem e nessuna dichiarazione di compliance GDPR. Restano OPEN #141, #109, #31, #32, #33 e #125.

## Riferimenti ufficiali

- PostgreSQL 17, sicurezza funzioni: https://www.postgresql.org/docs/17/perm-functions.html
- PostgreSQL 17, transazioni: https://www.postgresql.org/docs/17/tutorial-transactions.html
- PostgreSQL 17, comportamento delle sequence: https://www.postgresql.org/docs/17/functions-sequence.html
- PostgreSQL 17, RLS: https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- GDPR: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
