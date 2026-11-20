# ADR 0022 — Append locale autenticato del journal di recovery (solo harness sintetico)

**Data:** 2026-11-20  
**Stato:** Accepted per incremento #141 e test CI; NON è un publisher di cancellazioni di produzione.

## Contesto

ADR 0021 verifica un journal HMAC e un checkpoint separatamente
autenticato, ma non esisteva un componente .NET che aggiungesse eventi
preservando la catena precedente. Le fixture PITR venivano firmate
da uno script di test Python: non dimostravano che un vero produttore
riaprisse file persistiti senza riscriverne il contenuto.

## Decisione implementata

- `RecoveryJournalLocalAppender` è un **prototipo locale** di sola
  append per eventi `erase-actor`: genera un eventId UUID unico,
  calcola la firma HMAC-SHA256 e il nuovo digest chain usando il
  payload JSON canonico dell'ADR 0021.
- Il journal e il checkpoint devono essere percorsi assoluti, distinti,
  nella stessa directory esistente 0700 (su Unix) senza symlink.
  I file creati sono 0600, non esponibili da utenti group/other.
- Ogni append acquisisce un file lock esclusivo creato con CreateNew.
  Un lock preesistente (anche residuo di crash) nega la scrittura
  finché un operatore non verifica lo stato: nessun auto-break.
- Se entrambi i file esistono, **prima di qualsiasi modifica**
  vengono verificati col `RecoveryJournalVerifier` e le due chiavi
  HMAC diverse. File mancanti a metà, vecchia firma, checkpoint
  non coerente, journal troncato/esteso, percorsi non privati,
  chiavi insufficienti e identità invalide falliscono chiusi.
- Il primo append crea un journal nuovo; gli append successivi
  leggono e confrontano i byte già firmati, poi aggiungono una sola
  riga e invocano `FileStream.Flush(flushToDisk:true)`.
  Il checkpoint aggiornato è scritto in un file temporaneo 0600
  nella stessa directory, sottoposto a flush e sostituito con
  `File.Move(..., overwrite:true)`, poi riletto e verificato.
- Un crash **dopo** la riga aggiunta ma **prima** del checkpoint
  aggiornato lascia intenzionalmente una discrepanza: il prossimo
  tentativo rifiuta di continuare. Non viene troncatо né ricostruito
  automaticamente il giornale: un operatore deve investigare.
- `AppendFromPrivateFiles` legge chiavi binarie private da path
  espliciti e azzera i buffer sensibili in memoria.
- Il test PITR `Omnia.RestoreRehearsal -- append-journal` è limitato
  al database fittizio su 127.0.0.1:15432 e agli ID sintetici fissi.
  La CI genera le chiavi in runner privato e chiama l'appender
  .NET; poi `Omnia.RecoveryAudit` verifica, svuota intenzionalmente
  il journal per accertare il rifiuto e ripristina la fixture.
- I test comprendono append multipli, resume dopo un nuovo writer,
  verifica storico byte-for-byte, checkpoint mancante/manomesso,
  blocco lock preesistente, crash window simulato, permessi
  directory/file insicuri e due chiavi identiche/identificatori invalidi.

## Limitazioni non negoziabili

Il flush del file e il rename **non garantiscono una transazione
multi-file**; il test si aspetta il blocco, non il recovery automatico.
La libreria .NET non esegue fsync della directory e non implementa
un protocollo di lock distribuito o un WAL dedicato: un guasto di
alimentazione può lasciare il checkpoint non visibile o un lock stale.
Un attaccante con pieno accesso alla macchina può sostituire entrambi
i file o interferire nei passaggi fra check e open; una directory
0700 e il lock coordinano soltanto i writer cooperativi sullo stesso
host. La procedura non offre protezione autonoma contro rollback
del journal e del checkpoint insieme.

**Non è collegata alle cancellazioni reali `EraseAsync`, né dispone
di outbox transazionale o prova di emissione per ogni richiesta
autorizzata.** La firma HMAC dimostra solo possesso della chiave,
non identità o base giuridica del richiedente. Nessun servizio cloud
o rete, nessun ledger indipendente append-only, custody da HSM/vault,
anti-rollback monotono, rotazione/retention legale, release firmata
o traffic fence globale. API marker `ready\n` invariato, nessuna
autorizzazione automatica e nessun dato personale reale.

**#141, #31, #109 e #125 restano OPEN**; non dichiarare GDPR o DR
production-ready sulla base di questo test.

## Documentazione

- .NET 10 FileStream.Flush(true):
  https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0
- .NET File.Move(source, dest, overwrite):
  https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move
- HMAC RFC 2104: https://datatracker.ietf.org/doc/html/rfc2104
- PostgreSQL 17 PITR: https://www.postgresql.org/docs/17/continuous-archiving.html