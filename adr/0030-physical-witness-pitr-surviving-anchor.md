# ADR 0030 — PITR fisico del witness contro ancora HMAC superstite

**Data:** 2026-11-20
**Stato:** Accepted per esercizio di sicurezza sintetico PostgreSQL 17 (#141/#109); non production-ready.

## Contesto

ADR 0026 dimostra il PITR fisico del primario e il replay di erasure
consegnate al witness. ADR 0028–0029 introducono la verifica HMAC di un
prefisso witness e un terzo cluster PostgreSQL di CI per conservare il
checkpoint. La prova precedente del rollback del witness si basava
solo su uno snapshot in memoria alterato, non su un recupero da WAL.
Bisogna mostrare il rifiuto di un vero clone witness recuperato PRIMA
di una cancellazione già ancorata nel terzo cluster.

## Implementazione

1. Il container witness CI usa `archive_mode=on` e `archive_command`
   con directory temporanea riservata al runner. Il nuovo script
   `tests/restore/physical-witness-pitr-rehearsal.sh` richiede
   `GITHUB_ACTIONS=true`, `CI=true` e connessioni fisse locali.
   Crea un database `omnia_witness_pitr_anchor` nel terzo PGDATA
   15438, con ruoli/ACL di ADR 0029 ma indipendente dal database
   della suite xUnit.
2. `Omnia.WitnessPitrRehearsal` (.NET 10 test-only, NON API) esegue
   il bootstrap esplicito della nuova ancora con una chiave HMAC
   privata di 32 byte nel runner. Un `pg_basebackup` fisico del
   witness live è verificato con `pg_verifybackup`.
3. Dopo il backup viene creata una tabella marker e un record
   `before-target` nel WAL; segue
   `pg_create_restore_point('omnia_ci_before_witness_pitr_erasure')`.
   Il test inserisce con `PostgresErasureWitness.AppendAsync` un
   evento `erase-actor` SOLO sintetico, firma con il servizio
   `PostgresWitnessAnchorIssuer.IssueAsync` il nuovo prefisso dopo
   verifica della vecchia ancora e crea il marker `after-target`.
   Il witness live deve passare `PostgresWitnessAnchorVerifier`.
4. Dopo `pg_switch_wal()` e conferma del segmento archiviato, un
   container PostgreSQL 17 separato ripristina la basebackup e
   riapplica il WAL tramite `recovery.signal`,
   `restore_command`, `recovery_target_name`, `promote`.
   Si dimostra il punto esatto: marker prima presente, marker dopo
   assente, ricevuta post-punto assente, sequence inferiore al
   checkpoint della terza PGDATA.
5. Il clone deve avere lo STESSO `system_identifier` del witness
   source per attestare la natura fisica del restore; il cluster
   dell'ancora deve averne uno DIVERSO. Prima dell'audit la password
   del ruolo witness reader del clone viene ruotata e la vecchia
   credenziale deve essere negata.
6. `--verify-recovered` usa esclusivamente reader EXECUTE-only e
   una chiave HMAC preesistente. Richiede un rifiuto per divergenza
   del prefisso con la terza PGDATA. `--verify-source` riesce sul
   witness live; count e byte del checkpoint sulla terza PGDATA
   restano invariati, così un ripristino non ri-firma la storia
   regredita. Nessun file recovery `ready` viene generato.

## Verifiche TDD e limiti

RED: CI richiede il nuovo script prima dell'implementazione. GREEN:
il test produce un vero clone witness WAL e un verdetto di rifiuto
contro una receipt post-point autenticata. La suite di regressione
esistente di Domain, PostgreSQL integration, PITR primario e browser
deve restare verde su `main` prima di dichiarare il gate verificato.

**Fiducia limitata:** pur essendo tre PGDATA fisicamente distinti,
tutte le istanze e la directory HMAC/WAL vivono nel medesimo host
temporaneo GitHub Actions. Un operatore con accesso admin può
riportare indietro **anche la terza PGDATA**, o alterare la chiave;
non si verifica un'ancora offsite immutabile, un quorum/monotonic
counter remoto o un vault OpenBao/Azure Key Vault. Il commit sul
primario che non ha ancora ricevuto ACK witness può perdersi prima
di questo audit, senza prova di incompletezza. Nessuna protezione
degli ingress API/worker/repliche prima della riapertura, né
cancellazione su backup, media, device o provider AI esterni,
firma di autorizzazione o valutazione GDPR completa.
Restano aperte #141, #109, #31, #32, #33 e #125.

## Fonti ufficiali

- PostgreSQL 17, Continuous Archiving/PITR:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- PostgreSQL 17, pg_basebackup e verifica:
  https://www.postgresql.org/docs/17/app-pgbasebackup.html
- PostgreSQL 17, recovery target e WAL:
  https://www.postgresql.org/docs/17/runtime-config-wal.html
- GDPR artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
