# ADR 0020 — Stop cooperativo del worker retention durante la quarantena post-PITR

**Data:** 2026-11-19  
**Stato:** Accepted per slice incrementale di #141; non è un distributed recovery fence.

## Problema

ADR 0019 blocca l'avvio e le richieste HTTP quando `Recovery__AccessStateFile`
non contiene lo stato esatto `ready\n`, ma un `RetentionSweepService` già attivo
continua a chiamare la funzione PostgreSQL di eliminazione per TTL su timer.
La riapertura dopo PITR richiede almeno che il worker interno rispetti lo stesso
stato amministrativo; il solo gate HTTP non protegge i workload asincroni.

## Decisione

- `RecoveryGuardedRetentionCycle` riutilizza **esattamente**
  `RecoveryQuarantineGate.IsReady(config, isDevelopment)` per l'operazione
  retention, quindi gli stessi controlli su file assoluto, symlink,
  permessi Unix, formato rigido `ready\n` e assenza obbligatoria non-Development.
- Il ciclo controlla il file all'inizio **di ogni batch SQL**; file assente,
  file `quarantined\n` oppure corrotto -> 0 batch aggiuntivi.
  Non tenta di eseguire neppure la funzione SQL `omnia_purge_expired_memories`.
- Il worker già esistente resta limitato a max 10 batch da 500 righe con
  cancellazione solo degli scaduti e accesso dal ruolo `omnia_retention`
  non-owner senza SELECT diretta sulle tabelle personali. Sono mantenuti
  `CancellationToken`, timer ogni 15 minuti, log aggregato dei conteggi,
  nessuna inclusione di personal content nei log.
- Il ciclo si interrompe anche quando il file viene revocato **durante**
  un precedente batch; prima di un batch successivo rivaluta la quarantena.
  Il risultato aggregato include le cancellazioni già confermate.
- Il test TDD usa fixture PostgreSQL reali: dato scaduto protetto durante
  quarantena, poi eliminato dopo `ready\n`, e un dato non scaduto
  che permane. Test separato revoca il marker dopo il primo batch
  simulato e verifica che non esista un secondo batch.

## Limiti e sicurezza

Il controllo è **cooperativo a granularità di batch**, non atomico rispetto
all'istante in cui il marker cambia. Un comando SQL già in corso può
terminare dopo la revoca. Un ruolo con `EXECUTE` sulla routine SQL o accesso
diretto al DB può operare indipendentemente da questo gate. Nessun worker
offline, altra replica/servizio, provider esterno o richiesta in volo
è bloccato automaticamente. La funzione TTL stessa non incorpora un
lease/recovery epoch database-side e la chiave `ready\n` non ha firma.

Non cambia la responsabilità GDPR sulla retention, il trattamento dei backup
e gli eventuali obblighi di cancellazione. **#141/#31/#109 restano OPEN**
per journal indipendente/durabile, watermark anti-rollback, mutazioni
atomicamente recintate, gating di tutte le repliche e release autenticata.

## Fonti

- ASP.NET Core .NET 10 hosted services:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0
- PostgreSQL 17 row-level security:
  https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- GDPR art. 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/