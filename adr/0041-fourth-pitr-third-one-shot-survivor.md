# ADR 0041 — PITR fisico della quarta PGDATA: fence superstite sul terzo cluster

**Data:** 2026-11-20  
**Stato:** Accepted per test sintetico CI; NON production-ready.  
**Issue:** #141 (P0), #109, #31 e #32 (aperte).  
**Precedente:** [ADR 0040](0040-fourth-cluster-one-shot-fence-third-pitr.md).

## Minaccia

La ADR 0040 esercita un rollback fisico della TERZA PGDATA mantenendo
la QUARTA intatta. Rimane da testare il caso **simmetrico**: un PITR
della QUARTA cancella la prima ammissione one-shot dopo che la TERZA
ha ricevuto la stessa claim. Se si considera la sola QUARTA come fonte
di unicità, un retry dello stesso UUID appare nuovamente ammesso.
L'integrità di una singola PGDATA NON equivale a custodia anti-rollback.

## Decisione e ordine dei passi

1. Nella CI usa-e-getta abilitare archive_mode e archive_command
   soltanto sul container QUARTA (PostgreSQL 17, loopback 15440).
   Usare un database separato `omnia_ci_fourth_claim_pitr` con
   lo stesso schema `preerase-execution-claims.sql`, identità SQL
   writer/reader ristrette e UUID e chiave HMAC casuali.
2. Eseguire `pg_basebackup -X stream`, `pg_verifybackup`,
   marker pre-target, `pg_create_restore_point` e quindi
   l'ammissione **QUARTA → TERZA** attraverso
   `PostgresPreEraseExecutionClaimWriter.ClaimOnceAsync`.
   Controllare lo scope HMAC su entrambe le PGDATA.
   Solo dopo scrivere il marker post-target.
3. Archiviare il WAL e promuovere il clone della QUARTA su
   loopback 15442 al named restore point. Richiedere uguale
   system_identifier del QUARTO cluster e diverso dal TERZO,
   marker precedente presente, successivo assente e UUID
   assente nel clone ma ancora HMAC-autenticato nel TERZO.
4. Ruotare **entrambi** i login writer/reader ereditati dalla
   copia fisica e verificare che le vecchie credenziali falliscano.
   Attraverso le credenziali nuove e gli adapter ristretti,
   reclamare l'UUID sul clone QUARTA: questa prima scrittura
   è possibile proprio perché il PITR ha cancellato la claim.
   Richiedere poi che il TERZO **rifiuti** lo stesso UUID con
   SQLSTATE `23505`; autenticare di nuovo entrambi gli scope.
5. Nessun `DELETE` sulla primaria, nessuna modifica alle tabelle
   utente, nessun rilascio della quarantena, nessun percorso
   pubblico REST. La prova deve fallire se la TERZA perde il MAC
   oppure se la quarta copia non ha davvero perso la claim.

## Interpretazione corretta

Le due prove ADR 0040/0041 mostrano soltanto che, in una
*sequenza CI di ACK senza rollback contemporanei*, un rollback
**singolo** del TERZO o QUARTO cluster non rende un UUID riutilizzabile
attraverso entrambi i passaggi. Se retrocedono **entrambe**
le PGDATA, l'unicità può andare persa. Un acknowledgement può
precedere un errore/rollback del commit del DELETE sul PRIMO
cluster: **né un HMAC né un errore 23505 attestano il commit**.
Il quarto clone può ottenere una seconda claim, che resta
orfana quando la terza authority respinge il retry.

Tutti i cluster, segreti e WAL di questo test vivono nello
**stesso runner GitHub Actions**. Non esistono custodia offsite
immutabile (WORM), indipendenza amministrativa, attestazione
storica dei commit, 2PC, recupero vault/HSM, autorizzazione OIDC
del richiedente, controllo globale ingress/worker/repliche,
cancellazione provider/device/media/backup, né decisione
legale GDPR. Le issue #141/#109/#31/#32/#33/#125 restano aperte.

## Test e riferimenti

- `tests/restore/physical-fourth-claim-pitr-rehearsal.sh`
- `tests/Omnia.ClaimPitrRehearsal/Program.cs`
- `.github/workflows/ci.yml`: il gate viene eseguito dopo il PITR della TERZA.
- PostgreSQL 17 PITR/WAL:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- PostgreSQL 17 pg_basebackup:
  https://www.postgresql.org/docs/17/app-pgbasebackup.html
- PostgreSQL 17 pg_verifybackup:
  https://www.postgresql.org/docs/17/app-pgverifybackup.html
- PostgreSQL 17 UNIQUE:
  https://www.postgresql.org/docs/17/indexes-unique.html
- GDPR Regolamento (UE) 2016/679:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
