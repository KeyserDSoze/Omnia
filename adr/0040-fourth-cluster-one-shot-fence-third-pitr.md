# ADR 0040 — Quarta PGDATA di ammissione one-shot e PITR fisico del terzo cluster

**Data:** 2026-11-20  
**Stato:** Accepted per regressioni sintetiche TDD CI; NON pronta per produzione.  
**Issue:** #141 P0, #109, #31 e #32 (aperte).

## Contesto

ADR 0038–0039 memorizzano un tentativo di cancellazione
su un database della TERZA PGDATA, prima del COMMIT
del primario. Un singolo `pg_basebackup` + PITR della
TERZA PGDATA può però perdere la claim, rendendo
nuovamente disponibile lo stesso UUID, anche se
prima della perdita si erano verificati DELETE
e outbox. La protezione a consumo singolo
richiede un test di rollback della propria
autorità, non soltanto del primario.

## Decisione e ordinamento

1. Creare in CI una QUARTA istanza fisica PostgreSQL 17
   distinta con `system_identifier` univoco, porta
   loopback 15440, database `omnia_fourth_claims`,
   credenziali `omnia_claim_writer/reader` EXECUTE-only,
   schema di claim della ADR 0038, senza accesso
   alle tabelle raw e con MAC canonico di UUID,
   tenant e subject. Il CI verifica che l'identità
   fisica differisca da PRIMO/SECONDO/TERZO.
2. Introdurre `EraseWithFourthFenceAsync` SOLO per
   sintesi CI. Prima verifica l'intento ancorato
   della seconda/terza PGDATA. Poi chiama
   `ClaimOnceAsync` sulla QUARTA, e solo dopo
   `ClaimOnceAsync` sulla TERZA. Infine esegue
   DELETE dell'attore e inserisce lo stesso UUID
   nell'outbox in un unico commit primario.
   Entrambe le claim sono NON idempotenti
   (UUID duplicato → SQLSTATE 23505).
3. TDD RED: test xUnit richiede un metodo inesistente.
   GREEN: Postgres reale rifiuta writer QUARTA
   non autorizzato senza modifiche; accetta
   una prima esecuzione; tramite il superuser
   esclusivamente di test cancella la claim
   sul TERZO mantenendo intatta la QUARTA;
   un secondo DELETE dello stesso UUID è
   rifiutato dalla QUARTA anche se l'outbox
   source è disponibile e sono emersi nuovi
   consensi/memorie. Una claim QUARTA
   orfana dopo fallimento sulla TERZA è
   intenzionalmente non recuperabile con retry.
4. Un ulteriore test esegue una VERA
   basebackup fisica della TERZA PGDATA,
   `pg_verifybackup` e archiviazione WAL.
   Scrive un restore point nominato PRIMA
   di claim sintetica QUARTA → TERZA.
   Ripristina il clone PostgreSQL a prima
   di quella claim, lo promuove, confronta
   `system_identifier`, marker before/after,
   UUID mancante nel clone, ed HMAC ancora
   presente nella QUARTA. Le credenziali
   reader del clone sono ruotate e
   quelle ereditate dalla backup respinte.
   Tramite lo stesso adapter C# con
   ruolo SQL writer QUARTA, il duplicato
   fallisce con 23505. Il TEST NON invoca
   alcun DELETE source né emette `ready`.

## Garanzie e limiti

Il nuovo test rileva e previene un riutilizzo
quando **solo la TERZA** PGDATA retrocede mentre
la QUARTA rimane integra. Non garantisce
la stessa cosa se la QUARTA retrocede,
viene compromessa o non riceve l'ACK.
Un record di ammissione può esistere senza
un DELETE/COMMIT primario: la claim
non è prova di adempimento GDPR.
Nessun algoritmo atomico a 4 cluster
o transazione distribuita è presente;
ogni errore/timeout successivo all'ACK
sulla QUARTA rende l'esito INCERTO.
Un writer QUARTA compromesso può
preconsumare UUID e bloccare richieste.

Le quattro PGDATA condividono la stessa
macchina GitHub Actions, il controllo
dei container, il volume temporaneo e le
chiavi sintetiche. Non sono WORM/offsite
e non forniscono custodia amministrativa
indipendente, HSM/vault, autentica identità
OIDC del richiedente, attestazione
durevole indipendente del COMMIT sul
PRIMO cluster, gating integrale del
traffico REST/worker/repliche o
cancellazione di provider/backup/media/
device. Queste issue restano APERTE:
#141 P0, #109, #31, #32, #33 e #125.

## Fonti autorevoli

- PostgreSQL 17 — WAL archiving e PITR:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- PostgreSQL 17 — pg_basebackup:
  https://www.postgresql.org/docs/17/app-pgbasebackup.html
- PostgreSQL 17 — vincolo UNIQUE:
  https://www.postgresql.org/docs/17/indexes-unique.html
- PostgreSQL 17 — SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- GDPR Regolamento (UE) 2016/679:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
