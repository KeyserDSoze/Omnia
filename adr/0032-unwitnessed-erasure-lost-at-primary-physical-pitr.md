# ADR 0032 — Prova fisica della finestra erase commit → primo ACK witness

**Data:** 2026-11-20  
**Stato:** Accepted come NEGATIVE disaster-recovery rehearsal sintetica CI; **non** una soluzione anti-loss di produzione.  
**Issue:** #141 (P0), #109, #31; dipendenze #32/#33/#125.

## Problema

L'outbox transazionale del primario è atomica con la cancellazione,
ma la consegna verso il witness è necessariamente asincrona
(ADR 0027). Se il primario perde i WAL dopo il proprio commit ma
prima del primo ACK witness, il PITR può cancellare sia il DELETE
sia l'outbox. Nessun event UUID risulta nel witness. L'audit LIVE
del primario (ADR 0031) in questo scenario può riportare
`VerifyAllAsync` positivo, perché la storia che avrebbe dovuto
essere verificata non esiste più nel database recuperato.
Il PITR fisico precedente (ADR 0026) copriva invece solo
un `EraseAsync` GIÀ consegnato al witness.

## Decisione e prova

1. Estendere il REALE `tests/restore/physical-pitr-rehearsal.sh`
   (PostgreSQL 17 basebackup, pg_verifybackup, WAL archiving e
   named recovery point) con un SECONDO attore sintetico diverso
   dall'attore witnessed e dal peer, con memoria cifrata e
   consenso acquisiti PRIMA della basebackup.
2. Dopo il restore point e DOPO il ciclo publisher, chiamare
   davvero `PostgresPersonalDataService.EraseAsync` per il
   secondo attore, verificando con le tabelle source in CI che
   il dato sia cancellato e l'outbox sia committata. Il witness
   reader separato deve confermare che il suo UUID è ASSENTE.
   Non pubblicare intenzionalmente questo nuovo evento.
3. Conservare l'UUID sorgente soltanto in un file 0600 nella
   directory temporanea 0700 del runner, fuori dalla PGDATA;
   nessuna chiave o identificatore personale viene stampato nei
   log o caricato come artefatto. **Non è un ledger firmato,
   né un timestamp/cursore di commit indipendente affidabile.**
4. Il clone fisico recuperato sul vecchio punto WAL (15435)
   deve contenere nuovamente memoria cifrata e consenso di
   quell'attore, ma non la relativa outbox; il witness ancora
   non ha l'UUID. Lo script ruota le password recuperate del
   login runtime `omnia_app` e dispatcher `omnia_dispatcher`
   e rifiuta le password sorgente precedenti.
5. `Omnia.UnwitnessedPitrRehearsal` è una CLI test-only,
   non API, con opt-in CI esplicito e soli socket locali e
   ruoli EXECUTE-only. `--live-gap` richiede l'UUID source
   presente e receipt witness assente; `--recovered-gap`
   richiede source UUID + receipt witness ASSENTI. Esegue
   anche l'audit completo del source recuperato e dimostra
   che quest'ultimo può PASSARE sulle sole outbox superstiti.
   Usando l'UUID salvato fuori dal backup, la diagnostica
   NEGA che un audit apparentemente pulito sia una prova di
   assenza storica delle cancellazioni.
6. Tenere l'API esplicitamente in `quarantined`: la prova
   di recupero deve rifiutarne l'avvio e verificare che
   il marker non venga alterato. Replay idempotente di un
   ALTRO attore witnessed non deve cancellare implicitamente
   il secondo attore unwitnessed o nascondere il gap.
7. TDD: CI RED con fase `seed-unwitnessed` ancora assente;
   implementazione della fixture, della CLI diagnostica,
   del test di PITR fisico, del controllo credenziali e
   quarantena; verifica GREEN sulla pipeline reale prima
   di dichiarare completato l'incremento.

## Scelta e residui di rischio

Questa ADR **documenta e verifica una vulnerabilità di
completezza** nel protocollo attuale: non chiude la #141.
Un codice di audit di delivery che riporti successo non deve
mai diventare automaticamente un token di riapertura API.
L'UUID file privato mostra solo il caso riprodotto con un
runner sintetico; non sopravvive a un guasto indipendente
né prova che ogni erase committato abbia avuto una copia
esterna. È falso affermare che il test realizzi exactly-once,
synchronous commit cross-DB, firma da HSM, log WORM offsite,
DR di produzione, recovery traffic fence universale, o
diritto all'oblio GDPR soddisfatto. La soluzione richiede
un meccanismo esterno di provenienza/autorità degli intent
e commit, una policy in presenza di ACK mancanti,
consensi/autorizzazioni e rete di ingress/workers/repliche
coerentemente bloccata, gestione vault e governance GDPR.

**Manteniamo OPEN #141, #109, #31, #32, #33 e #125.**

## Riferimenti ufficiali

- PostgreSQL 17, WAL archiving e PITR: https://www.postgresql.org/docs/17/continuous-archiving.html
- PostgreSQL 17, restore point e impostazioni recovery: https://www.postgresql.org/docs/17/runtime-config-wal.html
- PostgreSQL 17, basebackup: https://www.postgresql.org/docs/17/app-pgbasebackup.html
- PostgreSQL 17, pg_verifybackup: https://www.postgresql.org/docs/17/app-pgverifybackup.html
- GDPR, artt. 5 e 17: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
