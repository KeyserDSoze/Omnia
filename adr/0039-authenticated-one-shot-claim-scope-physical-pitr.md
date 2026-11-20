# ADR 0039 — Validazione HMAC dell'ambito di un tentativo erasure a consumo singolo

**Data:** 2026-11-20  
**Stato:** Accepted, regressione sintetica CI con PostgreSQL 17; non production-ready.  
**Issue:** #141 P0, #109, #31, #32.

## Problema e minaccia

ADR 0038 registra nella TERZA PGDATA un tentativo
di cancellazione una tantum con UUID univoco e HMAC
di tenant/soggetto. L'adapter di sola lettura
`HasClaimAsync` verificava però solo l'esistenza
dell'UUID e il runbook fisico usava un conteggio
SQL da superuser. Un UUID con HMAC di un altro
attore, o contenuto modificato, risultava
ugualmente "presente". La presenza non può
diventare prova di corrispondenza dell'ambito,
tantomeno prova di commit sul primario.

## Decisione

1. Aggiungere `admission.claim_scope(uuid)` al
   database sintetico `omnia_execution_claims`
   (e al database PITR indipendente). Funzione
   SQL `STABLE SECURITY DEFINER`, con
   `SET search_path=pg_catalog,pg_temp`, che
   restituisce solo il `scope_hmac bytea`
   della richiesta o NULL se assente. Nessuna
   esposizione di tenant/owner in chiaro né
   SELECT diretto. NELLA STESSA transazione
   DDL revocare il default EXECUTE PUBLIC e
   concedere EXECUTE solo a `omnia_claim_reader`,
   non a `omnia_claim_writer`.
2. `PreEraseExecutionClaimScope.Compute` genera
   l'HMAC-SHA256 di JSON canonico con
   `schemaVersion=1`, purpose separato
   `omnia-preerase-single-use-claim-v1`,
   request UUID normalizzato, tenant e owner.
   Usare lo STESSO helper in writer e reader,
   evitando divergenze di serializzazione.
   Validare UUID, limiti di stringhe, key di
   esattamente 32 byte. Azzerare i buffer
   temporanei.
3. `PostgresPreEraseExecutionClaimReader.
   VerifyClaimAsync(uuid,tenant,owner,key)`
   richiede identità reader SQL EXECUTE-only,
   legge il MAC tramite la funzione e confronta
   in tempo costante con
   `CryptographicOperations.FixedTimeEquals`.
   Claim assente, MAC diverso, dimensione
   errata o chiave diversa: eccezione
   fail-closed; ruolo non autorizzato:
   `EXECUTION_CLAIM_ROLE_DENIED`.
   `HasClaimAsync` sopravvive solo come
   diagnostica booleana, non come attestazione.
   La stessa guardia verifica la nuova ACL
   sia per il reader sia per il writer.
4. Test RED → GREEN con PostgreSQL 17 reale:
   prima assenza del nuovo metodo, poi verifica
   di claim corretta, tenant/soggetto esteri,
   chiave errata, UUID mancante, writer che
   tenta il read e alterazione del MAC di
   un record sintetico tramite superuser TEST.
   Il booleano resta true per una riga alterata,
   ma il verificatore HMAC deve respingerla.
5. Il rehearsal di `pg_basebackup` e PITR fisico
   del PRIMARIO richiama l'audit read-only
   attraverso `omnia_claim_reader` separato.
   Prima del tentativo richiede claim assente;
   dopo la claim verifica HMAC di UUID/tenant/
   subject e rifiuta uno scope estraneo e
   reader impersonato da writer. Dopo PITR,
   quando il primario ha perso l'outbox,
   la claim sopravvissuta deve ancora
   autenticarsi con il MAC corretto sul
   TERZO PGDATA. Il servizio continua a
   rifiutare 23505 sul riutilizzo; nessuna
   cancellazione o `ready` è autorizzata.

## Non-obiettivi e lacune

La claim autenticata prova solo che un
tentativo è stato registrato con un dato
ambito. **NON prova che il DELETE source
sia stato committato**, che il richiedente
fosse autorizzato legalmente o che siano
stati cancellati dati su tutti i provider.
Un superuser del DB terzo può alterare
o ripristinare la tabella, e un avversario
con la chiave può calcolare un nuovo HMAC.
Non c'è WORM/offsite, HSM/vault, custodia
con dominio amministrativo separato,
2PC, autenticazione OIDC del richiedente,
fencing globale API/worker/repliche
o verifica completa GDPR. Tutte le
PGDATA risiedono nello stesso runner
CI. Le issue #141/#109/#31/#32
rimangono APERTE.

## Riferimenti ufficiali

- PostgreSQL 17, CREATE FUNCTION e SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17, revoca dei privilegi di funzione:
  https://www.postgresql.org/docs/17/ddl-priv.html
- PostgreSQL 17, verifica di privilegi:
  https://www.postgresql.org/docs/17/functions-info.html
- Regolamento UE 2016/679, artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
