# ADR 0027 — Publisher autonomo CI-only con retry e ricevute witness persistenti

**Data:** 2026-11-20
**Stato:** Accepted come ulteriore prova sintetica (#141/#109), non come publisher di produzione.

## Contesto

ADR 0023-0026 consegnano eventi di cancellazione committati sul
primario a un PostgreSQL witness distinto, ma richiedono un'invocazione
manuale/test-only. Una perdita di connessione o un riavvio tra commit
outbox e append witness lascia un evento non consegnato. Il PITR del
primario può poi cancellarlo con la relativa outbox.

## Decisione

1. Aggiungere un eseguibile .NET 10 **indipendente**,
   `src/backend/Omnia.ErasurePublisher`, non registrato in Omnia.Api,
   senza endpoint HTTP, credenziali personali o azioni verso provider.
   Per questa slice il processo richiede `CI=true` e
   `OMNIA_ERASURE_PUBLISHER_CI_ONLY=1`; controlla host
   `127.0.0.1`, porte, nomi DB e login fissi di test, oltre a
   `PostgresErasureDispatchReader.VerifyRoleAsync` e
   `PostgresErasureWitness.VerifyWriterAsync`.
2. Il ciclo usa `PostgresErasureOutboxWitnessPublisher` esistente
   (max 1000 eventi/ciclo; max 100 per pagina). `--once` esercita
   una scansione verificabile in CI; la modalità persistente usa
   una pausa di 5s, retry degli errori e backoff esponenziale con
   limite di 60s. Messaggi di esito non espongono identificatori o
   eccezioni SQL. Una failure non è una conferma.
3. Rileggere sempre l'outbox dall'inizio. `(created_at,id)` serve
   solo per paginare **una** scansione ed è esplicitamente inadatto
   come cursore persistito di commit; gli eventi possono committare
   in ordine differente da quello temporale. Mai cancellare righe
   sorgente a seguito di ACK.
4. La conferma è il commit della funzione
   `witness.append_erasure` nel **secondo** PostgreSQL: UUID stabile,
   sequence allocata sotto `FOR UPDATE`, dedup idempotente e
   rifiuto di UUID in conflitto. Se la risposta va persa, la
   successiva scansione ripete l'evento e riottiene la stessa
   sequence; nessun ACK in DB primario è introdotto.
5. TDD: test RED su loop mancante; prova PostgreSQL con witness
   irraggiungibile e nessuna ricevuta, poi consegna, nuova istanza
   del publisher, sequenza e payload invariati. Pipeline compila
   la nuova CLI e prova rifiuto configurazione e `--once` valido.
   Controllare lo stato Actions prima di attestare successo.

## Limitazioni e sicurezza

Le prove sono su **due container nello stesso runner usa-e-getta**.
La ricevuta del witness è persistente *all'interno di quel cluster*
ma non è un ACK immutabile offsite, una firma dell'utente o un
checkpoint anti-rollback custodito separatamente. La finestra
**commit erasure → primo ACK witness** resta vulnerabile a perdita
del primario: scheduling e retry non eliminano questo rischio.
Nessuna certezza di consegna dentro uno SLO durante blackout
prolungati, numero eventi superiore al bound, cancellazione
dell'outbox, contemporanea perdita dei cluster o backup witness
manomesso. Non esistono lock distribuiti/lease, metriche permanenti,
alert, high-watermark indipendente, traffico/repliche/worker fence
globale, vault, WORM, trattamento provider/cache/media/device
oppure release authorization. I metadati witness sono personali,
soggetti a revisione base giuridica, retention, export e cancellazione
(GDPR art. 17). Restano aperte #141, #31, #109, #32, #33, #125.

## Riferimenti ufficiali

- PostgreSQL 17, transazioni e commit:
  https://www.postgresql.org/docs/17/tutorial-transactions.html
- PostgreSQL 17, durabilità e synchronous_commit:
  https://www.postgresql.org/docs/17/wal-async-commit.html
- Microsoft .NET 10, background tasks e arresto con cancellation token:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0
- GDPR art. 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
