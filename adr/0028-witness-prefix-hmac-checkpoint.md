# ADR 0028 — Checkpoint HMAC di prefisso witness e controllo rollback sintentico

**Data:** 2026-11-20
**Stato:** Accepted per verifica sintetica CI (#141/#109/#31); non production-ready, non autorizza release.

## Contesto

L'ADR 0027 ritenta la consegna di erasure events a un witness separato.
L'UUID idempotente e la sequence witness sopravvivono a un semplice
PITR del primario dopo la consegna, ma il witness stesso potrebbe essere
ripristinato a un punto precedente senza che un altro sistema lo
rilevi. Un contatore `next_sequence` nella stessa PGDATA NON è una
ancora anti-rollback indipendente.

## Decisione e comportamento

1. `PostgresErasureWitnessSnapshot.ReadAsync` usa esclusivamente
   `omnia_witness_reader` (EXECUTE-only su funzione bounded) e verifica
   il ruolo prima di ogni scansione. Usa una singola transazione
   PostgreSQL 17 `REPEATABLE READ`, `SET TRANSACTION READ ONLY`
   attraverso tutte le pagine (max 100 per query), preservando uno
   snapshot coerente anche con append concorrenti. Richiede sequenze
   contigue da 1, UUID distinti, event type `erase-actor` e un massimo
   di 10000 ricevute, con ulteriore lettura limitata per riconoscere
   overflow invece di attestare una scansione parziale.
2. `WitnessCheckpoint` serializza il payload dei singoli receipt in
   JSON UTF-8 canonico e collega i record tramite SHA-256 e
   `previousDigest`, a partire da un genesis versionato. La
   ricevuta include nel digest sequence, UUID, tenant, subject, tipo,
   ordine e prefisso. Il checkpoint NON contiene questi ultimi
   identificatori in chiaro, ma solo `schemaVersion=1`, `count`,
   `tipDigest` e `mac`, dove `mac` è HMAC-SHA256 con una
   chiave esterna di 32 byte. Verifica exact schema, bytes canonici,
   HMAC con confronto a tempo costante, digest completo del prefisso
   1..N e rifiuto di duplicazioni, gap e alterazioni.
3. Un checkpoint firmato su N ricevute autentica soltanto quelle N:
   il witness può successivamente contenere N+k eventi non ancora
   attestati. Questo controllo NON è una prova del completeness
   del primario, né di una cancellazione non ancora pubblicata.
4. `Omnia.WitnessAudit` offre `--issue` / `--verify`, solo con
   `CI=true`, esplicito `OMNIA_WITNESS_AUDIT_CI_ONLY=1` e connessione
   vincolata al reader 127.0.0.1:15436/omnia_witness. Una issue crea
   un nuovo file privato in directory privata, senza sovrascrivere
   mai un checkpoint precedente. La verifica esige file e chiave
   HMAC privati separati, fallisce su assenza, alterazione o ruolo
   sbagliato, e non modifica mai l'API né il file `ready`.
5. Test RED preliminari definiscono manomissione/riordino/troncamento,
   ID duplicati e login illecito. PostgreSQL vero usa receipt
   inserito con la normale append del witness; CI verifica
   issue/verify e fallimenti del percorso file. Le evidenze CI
   saranno registrate nella #141 dopo conclusione delle Actions.

## Limiti che impediscono un uso reale

File e chiave HMAC sono nel runner CI, quindi NON sono un high-watermark
monotono in un trust domain indipendente, un vault, una firma di
un'autorità di recupero, un ledger WORM/offsite, una protezione contro
il restore di entrambi gli store o un'autorizzazione a togliere
quarantena. Un nuovo checkpoint generato dopo il rollback
autenticherebbe soltanto lo stato *ripristinato*: serve un'autorità
che conservi un attestato FRESCO precedente al disastro e impedisca
regressioni. Resta il buco commit outbox nel primario → prima
consegna witness, con perdita irreversibile dopo un PITR prematuro.
Mancano gestione chiavi Key Vault/OpenBao, isolamento multi-host/geo,
checkpoint operativamente monotoni, audit/alert, RPO/RTO, enforcement
globale su API/worker/repliche/provider e modello di retention/GDPR
per i metadati dei receipt.

Restano OPEN #141, #109, #31, #32, #33, #125.

## Riferimenti ufficiali

- PostgreSQL 17, SET TRANSACTION e snapshot:
  https://www.postgresql.org/docs/17/sql-set-transaction.html
- PostgreSQL 17, consistenza applicativa:
  https://www.postgresql.org/docs/17/applevel-consistency.html
- .NET 10, HMAC-SHA256:
  https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hmacsha256.hashdata?view=net-10.0
- .NET, confronto costante:
  https://learn.microsoft.com/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals
- GDPR artt. 5/17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
