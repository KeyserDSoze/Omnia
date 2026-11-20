# ADR 0042 — PITR fisico simultaneo delle due authority di claim

**Data:** 2026-11-20  
**Stato:** Accepted come regressione **NEGATIVA** sintetica CI; NON production-ready.  
**Issue:** #141 P0, #109, #31 e #32 (aperte).  
**Precedenti:** [ADR 0040](0040-fourth-cluster-one-shot-fence-third-pitr.md), [ADR 0041](0041-fourth-pitr-third-one-shot-survivor.md).

## Problema

Le ADR 0040 e 0041 verificano la sopravvivenza di UNA claim authority
quando l'altra retrocede fisicamente a prima dell'ammissione. Ma
TERZA e QUARTA PGDATA, pur avendo `system_identifier` distinti,
condividono **runner, potere amministrativo e dominio di guasto**.
Un rollback fisico coordinato di ENTRAMBE può cancellare lo stesso
request UUID da entrambe le tabelle `admission.execution_claims`.
In questo scenario lo schema one-shot ammette un secondo tentativo
con lo stesso UUID: i singoli vincoli UNIQUE non ricordano ciò che
è stato perso col PITR.

## Decisione: controesempio reale, NON workaround

1. Aggiungere lo script CI-only
   `tests/restore/physical-dual-claim-pitr-rehearsal.sh` dopo i
   due test fisici single-cluster. Creare un database sintetico
   isolato per ogni authority sul TERZO e QUARTO container,
   rispettivamente porte 15438 e 15440, senza toccare i database
   delle altre prove.
2. Prima dell'ammissione prendere due veri
   `pg_basebackup --wal-method=stream`, verificarli tramite
   `pg_verifybackup`; creare un marker precedente e un named
   WAL restore point **su ciascuna authority**.
3. Attraverso la CLI sintetica, identità `omnia_claim_writer`
   EXECUTE-only, UUID identico e chiave HMAC privata e casuale,
   registrare claim QUARTA → TERZA; verificare scope canonico
   con `omnia_claim_reader`. Registrare due marker successivi
   e attendere archiviazione WAL post-target su entrambe.
4. Promuovere due **copie fisiche** PostgreSQL separate,
   con `system_identifier` uguale al rispettivo originale
   e diverso fra le authority. Porte 15443/15444,
   marker pre-target presente e post-target assente,
   `pg_is_in_recovery()=false`, UUID perduto in ENTRAMBE.
   Verificare che i due originali continuino ad autenticare
   la claim HMAC dell'esatto soggetto.
5. Ruotare entrambi i login writer/reader sui **due** cloni,
   rifiutare le credenziali reader ereditate e non fornire
   all'adapter credenziali superuser. Attraverso gli stessi
   adapter `ClaimOnceAsync` dimostrare che i due cloni
   **riaccettano entrambi lo stesso UUID**; verificarne HMAC.
6. La CLI del controesempio restituisce volontariamente
   **exit 42 (non-zero)** e la sentinella
   `DUAL_PITR_BOTH_CLAIMS_CAN_BE_REUSED_NO_RELEASE`.
   Il shell harness deve richiedere precisamente quell'esito
   come regressione NEGATIVA e segnalare esplicitamente
   `PHYSICAL_DUAL_CLAIM_PITR_REMAINED_QUARANTINED`.
   Non eseguire DELETE sul primario, non creare receipt,
   non riaprire API/worker e non confondere il passaggio
   del test CI con una barriera contro il rollback doppio.

## Cosa dimostra e cosa NON dimostra

Questo è un **controesempio verificabile** alla sufficienza
delle due claim authority: un ripristino coordinato cancella
entrambi gli storici, dunque le due scritture UNIQUE
possono riuscire di nuovo. Il run CI supera il test
*se e solo se* rivela il problema e la procedura resta
in quarantena. Un'aggiunta di ulteriori PGDATA sotto la
stessa custodia non risolve l'indipendenza del dominio di
guasto; occorre una fonte durevole non riscrivibile/
anti-rollback fuori dal controllo degli amministratori
dei cluster, con verifica di aggiornamento e monotonicità.

Non esistono offsite/WORM, vault/HSM produttivi,
attestazione esterna del **COMMIT DELETE del primario**,
decisione firmata e verificata di rilascio, prova di
legittimazione OIDC, fencing ingress/worker/repliche,
propagazione cross-store o approvazione GDPR. Un UUID
consumato è **solo un tentativo**, non l'adempimento
di una richiesta di cancellazione. #141, #109, #31,
#32, #33 e #125 rimangono aperte.

## TDD e fonti ufficiali

- Test RED prima della nuova CLI: `--dual-claim` e
  `--dual-probe-recovered` inizialmente inesistenti.
- Nuove fasi C# esclusivamente in
  `tests/Omnia.ClaimPitrRehearsal/Program.cs`.
- Le prove e i SHA delle Actions saranno registrati
  sulle issue solo dopo verifica reale del workflow.
- PostgreSQL 17 [PITR / WAL](https://www.postgresql.org/docs/17/continuous-archiving.html),
  [pg_basebackup](https://www.postgresql.org/docs/17/app-pgbasebackup.html),
  [pg_verifybackup](https://www.postgresql.org/docs/17/app-pgverifybackup.html),
  [unique indexes](https://www.postgresql.org/docs/17/indexes-unique.html).
- [GDPR (UE) 2016/679, art. 17](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/).
