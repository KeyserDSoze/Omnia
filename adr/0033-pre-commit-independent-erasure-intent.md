# ADR 0033 — Intento di cancellazione pre-commit nel witness indipendente (solo CI)

**Data:** 2026-11-20  
**Stato:** Accepted, test sintetico di sicurezza (#141); NON protocollo reale di commit o rilascio DR.

## Problema e semantica

La ADR 0032 prova che il primario può confermare `EraseAsync`
e perdere con PITR sia il DELETE sia l'outbox PRIMA del primo
ACK remoto, mentre il witness post-commit non conosce l'evento.
Serve sperimentare una **traccia di richiesta antecedente al
DELETE** su una PGDATA indipendente per osservare un caso
che altrimenti apparirebbe source-only "pulito".

**Non confondere tre eventi distinti:**
- `intent`: richiesta/azione desiderata, precedente al DELETE,
  non attestazione di autorità o successo dell'operazione;
- `primary COMMIT + outbox`: applicazione del DELETE nel database
  primario (perdibile a un PITR del primario);
- `witness erasure receipt`: ricezione idempotente del fatto
  già committato, dal publisher asincrono.

Un intento può esistere anche se il successivo `EraseAsync`
fallisce o non viene mai chiamato. L'UUID di intent non è
l'UUID outbox generato dal servizio source; non c'è ancora
un protocollo di correlazione autenticato end-to-end.

## Decisione testata

1. Nel SECONDO PostgreSQL 17 sintetico della CI creare
   `witness.erasure_intents` e un distinto
   `witness.intent_head` (contatore gapless con lock
   `FOR UPDATE`, INSERT idempotente per request UUID,
   conflitto di scope negato e validazione di tenant/owner).
   Le tabelle non attribuiscono alcun permesso a PUBLIC
   né ai ruoli erasure receipt originali.
2. Creare nuovi account
   `omnia_witness_intent_writer` e
   `omnia_witness_intent_reader` senza proprietà, BYPASSRLS
   o accesso tabellare. Il writer può solo EXECUTE
   `witness.append_erase_intent(uuid,varchar,varchar)`;
   il reader può solo EXECUTE `find_erase_intent` e
   `list_erase_intents` (limit 1–100).
   Funzioni SECURITY DEFINER con `search_path` sicuro,
   revoca PUBLIC e nomi qualificati.
   Gli adapter .NET verificano i ruoli **a ogni
   operazione**, rifiutando l'admin, il ruolo
   witness post-commit e i privilegi diretti.
3. Il PITR fisico del primario (ADR 0032) avvia con
   `--record-intent` SOLO per l'attore sintetico
   `omnia-ci-unwitnessed-pitr-synthetic`.
   Prima del DELETE, il runner genera un request UUID
   privato e registra l'intento su seconda PGDATA.
   Un writer errato o flag CI mancante deve causare
   il rifiuto PRIMA che il codice source cancelli
   memoria o consensi.
4. Effettuare poi il reale `EraseAsync` sul primario
   dopo restore point e dopo ultima esecuzione publisher,
   senza ACK post-commit; ripristinare il primario
   via pg_basebackup/WAL. Verificare che dati+consensi
   ricompaiano, outbox e receipt post-commit non
   contengano l'UUID, mentre `--recovered-gap`
   vede ancora il request intent sul witness rimasto
   non ripristinato. Conservare la quarantena API;
   nessuna cancellazione automatica tramite intent.
5. TDD RED: test `IndependentPreEraseIntentTests`
   richiede writer/reader ancora inesistenti;
   GREEN: test reali di PostgreSQL per UUID
   idempotente, conflitto, limiti, separazione dei
   ruoli, assenza dell'ACK e PGDATA diversa; test
   PITR fisico con handshake e verifica remota.
   Source test runner non stampa dati/UUID.
   Documento e issue vengono aggiornati dopo
   verifica delle Actions del commit finale.

## Trade-off e rischi aperti

Un write remoto prima del DELETE non è un commit
atomico cross-DB. Né PostgreSQL source né witness
hanno qui un transaction coordinator (2PC).
`witness.intent_head` può retrocedere se il
witness viene ripristinato; la terza PGDATA
di ADR 0029 ancora solo i **completed erase
receipts**, non la nuova sequenza di intent.
Il richiedente non è autenticato nel ledger
CI e il codice API di produzione non esegue
il preflight: la prova non impone che ogni
cancellazione reale venga preceduta da un
intento affidabile.

L'intento "orfano" non autorizza eliminazioni
senza base giuridica e consenso effettivo.
Per un DR reale restano necessari identità
richiedente verificabile, sistema di intent
e commit provenance indipendente/durevole,
riconciliazione degli esiti incerti,
WORM/offsite e chiavi in vault/HSM, fencing
di ingress/API/worker/repliche, cancellazione
di backup/media/cache/provider/device e
analisi GDPR per conservazione di metadata.
Il test **non abilita `ready`** e la #141
rimane aperta P0.

## Fonti ufficiali

- PostgreSQL 17, `SECURITY DEFINER`:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17, privilegi sulle funzioni:
  https://www.postgresql.org/docs/17/perm-functions.html
- PostgreSQL 17, Two-Phase Transactions:
  https://www.postgresql.org/docs/17/two-phase.html
- PostgreSQL 17, transazioni e commit:
  https://www.postgresql.org/docs/17/tutorial-transactions.html
- GDPR, artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
