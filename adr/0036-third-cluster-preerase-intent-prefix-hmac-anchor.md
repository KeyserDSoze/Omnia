# ADR 0036 — Ancora HMAC indipendente per il prefisso degli intenti pre-erase

**Data:** 2026-11-20  
**Stato:** Accepted per i test sintetici TDD/PostgreSQL 17 di #141; NON un ledger offsite e NON una prova del COMMIT source.

## Problema

Le ADR 0033–0035 registrano sul witness un intento
di cancellazione pre-commit, correlato tramite UUID alla
outbox del primario e letto soltanto da un reconciler
che non può mai emettere `ready`. Il witness era già
protetto da un HMAC e da un terzo PGDATA per gli eventi
di cancellazione **COMPLETATA** (ADR 0028–0030), ma
non per i nuovi `witness.erasure_intents`. Un PITR
del witness poteva quindi eliminare un pre-intent
senza essere rilevato dai controlli precedenti.
Conservare soltanto una copia del contatore nella
stessa PGDATA non serve da barriera anti-rollback.

## Decisione

1. `IntentCheckpoint.Create/Verify` firma un prefisso
   CONTIGUO del registro `witness.erasure_intents`
   usando HMAC-SHA256 con chiave da 32 byte, GENESIS
   separato e `purpose=pre-erase-intent-prefix`.
   Formato JSON canonico con esattamente 5 attributi,
   dimensioni limitate, verifica constant-time del MAC
   e del digest, UUID univoci, sequence 1..count,
   tipo `erase-actor-intent` e tenant/owner validi.
   Il payload ancorato contiene solo versione,
   purpose, count, digest e MAC, senza dati attore
   in chiaro. Un checkpoint di completed erasure
   NON è intercambiabile con quello di intent.
2. `PostgresPreEraseIntentSnapshot.ReadAsync` usa
   l'identità SQL `omnia_witness_intent_reader`
   EXECUTE-only, una sola transazione PostgreSQL
   `REPEATABLE READ + READ ONLY`, paginazione
   bounded 1–100, massimo 10000 ricevute più
   una interrogazione extra nel medesimo snapshot,
   con rifiuto di overflow, gap e UUID duplicati.
   Nessuna lettura diretta delle tabelle sottostanti.
3. `PostgresPreEraseIntentAnchorIssuer` legge
   lo stato firmato pre-esistente da un database
   distinto `omnia_intent_anchor` sulla TERZA PGDATA.
   Se l'ancora manca, `IssueAsync` NEGA:
   l'inizializzazione usa `BootstrapAsync`
   esplicitamente autorizzato solo dal test CI.
   Ogni avanzamento richiede verificare il prefisso
   PRECEDENTE contro il witness PRIMA di firmarne uno
   successivo. Riutilizza il protocollo SQL con lock
   di `anchor.head` e monotonic count, rifiuta
   regressione o payload diverso a count uguale.
   Writer e reader usano ruoli terzi EXECUTE-only;
   SQL owner/admin non sono ammessi dagli adapter.
4. Nel runbook di PITR fisico del WITNESS si crea
   un altro database sulla terza PGDATA chiamato
   `omnia_witness_pitr_intent_anchor`, con chiave
   separata. Prima della basebackup del witness
   si aggiunge un intent sintetico, poi si firma
   esplicitamente il primo prefisso. Dopo il
   restore point nominato WAL si aggiunge un
   secondo intent e si avanza il checkpoint.
   Il clone PostgreSQL 17 recuperato al punto
   precedente NON deve contenere questo evento,
   mentre il terzo PGDATA conserva digest/MAC.
   Il test controlla che il clone abbia ancora
   il system identifier del witness originale,
   che il contatore intents recuperato sia minore
   dell'ancora e che il verifier read-only
   **RIFIUTI** il rollback, mentre accetta il
   witness LIVE. La password intent reader del
   clone viene ruotata, e quella ereditata dal
   backup deve essere negata. Count e byte
   HMAC dell'ancora restano invariati.
5. TDD: test RED richiede nuove classi
   checkpoint/snapshot/issuer/verifier assenti;
   GREEN verifica PostgreSQL reale, terzo
   system_identifier indipendente, bootstrap
   fail-closed, autenticazione MAC e digest,
   prefisso troncato o alterato, chiave errata,
   ruolo amministratore negato e head
   monotona. PITR fisico verifica in più
   la regressione vera con WAL, senza
   autorizzare alcuna cancellazione automatica.

## Confini e minacce ancora aperte

**L'intento NON è una prova del DELETE o della
transazione COMMIT sul primario.** Può esistere
senza DELETE, ed essere legittimo o forgiato;
il suo UUID non dimostra la richiesta OIDC
legalmente autenticata. L'ancora documenta
solo il prefisso salvato sulla TERZA PGDATA;
tra `append intent` e `advance checkpoint`
rimane una finestra di perdita, in assenza
di protocollo distribuito o attestazione
sincrona esterna. L'ancora può essere
ripristinata assieme a witness se il medesimo
amministratore controlla entrambi: tutte
le tre PGDATA e chiavi risiedono in CI
sullo stesso host. Non c'è WORM/immutabilità
offsite, key vault/HSM, autorità di commit
e ingress/API/worker/replica release gate
integrale. Non sono ancora verificate le
cancellazioni di backup, media, provider AI,
cache o device né la conformità GDPR
Art. 5 e 17. Tutte le relative issue
(#141, #109, #31, #32, #33, #125)
restano APERTE e nessun `ready` è emesso.

## Fonti

- PostgreSQL 17, REPEATABLE READ e READ ONLY:
  https://www.postgresql.org/docs/17/sql-set-transaction.html
- PostgreSQL 17, snapshot consistenti:
  https://www.postgresql.org/docs/17/applevel-consistency.html
- PostgreSQL 17, privilegi e SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17, PITR fisico:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- GDPR Reg. UE 2016/679:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
