# ADR 0011 — Write-path sealed AES-GCM su memory_entries e lettura legacy temporanea

**Data:** 2026-11-18  
**Stato:** Accepted SOLO per sotto-issue #139 di #138/#33; non è readiness/security sign-off.

## Contesto

Il codec #137 fornisce AEAD, ma attualmente `memory_entries` riceve raw, normalized JSON, summary e tags come campi plaintext. Non possiamo completare in una sola modifica la cifratura di vecchi record, vettori ANN e backup. Occorre un primo cutover che protegga almeno tutte le **nuove scritture** e impedisca regressioni da vecchie istanze durante rollout, con piena compatibilità di export, RLS e retention.

## Decisione

- Nuova migrazione forward-only `0005_memory_sealed_payload.sql`: colonna `sealed_payload bytea`, check che impone placeholder `[sealed]`/JSON vuoto/tags vuoti per righe con ciphertext; trigger BEFORE INSERT OR UPDATE che respinge nuovi dati plaintext e downgrade/mutazioni sensitive di legacy senza nuova busta. I legacy esistenti rimangono `sealed_payload IS NULL` sino al backfill, senza cifratura fittizia.
- `PostgresMemoryRepository` opera sotto `PostgresActorScope`+advisory actor lock, legge il vero `id` esistente per l'upsert idempotente (associato alla AAD) e cifra il documento contenente raw, normalized JSON, summary e tags con `SealedMemoryPayload` e port crypto #137. Tutte le colonne legacy delle nuove note sono redatte (non una seconda copia in chiaro). L'assenza di key ring valida impedisce la scrittura e l'avvio API, senza fallback plaintext.
- `RecentAsync` usa RLS e decripta soltanto i campi per timeline; `PostgresPersonalDataService` recupera raw+derivati per JSON export e scrive `storageProtection=sealed-v1|legacy-plaintext`. Corruzione AEAD genera errore, niente fallback. TTL e DELETE eliminano l'intera riga con busta e vettore.
- Per test/Docker viene montato un JSON key ring fuori dal repository, con permessi minimi; CI crea chiave casuale effimera per ogni run, OIDC browser E2E valida il flusso reale. Nessuna KEK di default, nessun segreto nelle issue.

## Alternative scartate e rischi

Cifrare solo raw lasciando summary duplicata in chiaro è insufficiente: proteggiamo anche i quattro campi più ricchi di contenuto. Aggiungere solo la colonna cifrata mantenendo plaintext duplicato offrirebbe sicurezza fittizia, quindi redigiamo le colonne originali e facciamo enforcement nel DB. Non usiamo SQL pgcrypto con chiave passata al DB, evitando key material lato query.

Restano in chiaro **legacy prima della migrazione, embedding pgvector, source/externalId/contentType/embedding_model, tempi**. Per questi campi serve un modello dati privacy-by-design e una soluzione realistica per ricerca vettoriale. La API contiene ancora un datasource di migrazione privilegiato e lo stesso processo ospita il worker; il file locale non è un vault. I backup esistenti possono contenere plaintext. Nessuna migrazione dei record storici, nessun rewrap persistito, nessuna parità Azure, nessuna garanzia GDPR. Il parent #138 e #33 rimangono aperti.

## Prove

TDD RED e GREEN con PostgreSQL pgvector role RLS, tamper/tenant/summary/export/erasure, run OIDC Keycloak reale e Docs/AGENTS. Registrare URL SHA delle Actions nell'issue #139.
