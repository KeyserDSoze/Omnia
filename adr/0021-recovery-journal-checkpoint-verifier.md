# ADR 0021 — Verificatore offline di journal post-PITR con checkpoint autenticato

**Data:** 2026-11-20  
**Stato:** Accepted per slice di sicurezza e test della #141; NON è un ledger affidabile di produzione, né una release automatica.

## Contesto

Le ADR 0018–0020 hanno introdotto una ricevuta sintetica HMAC
e un marker operativo `ready\n` che blocca API e retention per batch.
Un singolo file di ricevute integro non garantisce completezza: potrebbe
essere troncato, sostituito con una copia precedente o avere eventi
omessi. Serve una verifica della sequenza contro un checkpoint esterno
e indipendentemente autenticato, separata dalle mutazioni sul database.

## Decisione (primo contratto, verifica di sola lettura)

- La classe `RecoveryJournalVerifier` nella Infrastructure verifica un
  journal UTF-8 JSON Lines versionato e delimitato obbligatoriamente
  da newline LF. Ciascuna riga descrive un evento `erase-actor` con
  `schemaVersion=1`, sequenza contigua da 1, eventId UUID N unico,
  tenant/subject, `previousDigest` e HMAC-SHA256 del payload canonico.
- La forma canonica v1 del payload è **JSON UTF-8 compatto, campi in ordine fisso e caratteri Base64 stampabili non escaped** (esplicitamente configurato con `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` su .NET, mai usato come HTML). La fixture Python di CI genera esattamente gli stessi byte; un test protegge i caratteri `+`, `/` e `=`. Il MAC autentica il payload canonico, non un rendering HTML.
- La catena collega gli eventi mediante `SHA256(canonicalPayload || HMAC)`;
  il primo evento fa riferimento a un digest genesis deterministico.
  Vengono rifiutati sequenze saltate, duplicati, riordinamenti,
  variazioni del contenuto, chiavi errate, proprietà JSON extra o
  duplicate, versioni ignote, linee vuote e UTF-8 scorretto.
- Il **checkpoint distinto** contiene `schemaVersion`, count, tipDigest
  e MAC, autenticato con una **seconda chiave HMAC da 32 byte diversa
  da quella del journal**. La verifica del checkpoint precede la
  lettura dei record e il conteggio/tip devono combaciare
  esattamente con la catena. Se una riga viene rimossa, la verifica
  del checkpoint non permette una falsa promozione del journal.
- È una verifica BOUNDED: 1–1000 eventi, 1 MiB massimo di journal,
  4 KiB massimo di checkpoint, 1024 byte per riga e chiavi di esatti
  32 byte. Ogni fallimento genera il solo errore generico
  `RECOVERY_JOURNAL_INVALID`; nessun identificatore viene loggato.
- Il `RecoveryJournalFileAudit` accetta solo file regolari di path
  assoluto, con permessi privati Unix, senza symlink; legge stream
  limitati e azzera in memoria chiavi e buffer dopo la verifica.
- `Omnia.RecoveryAudit` è una CLI **offline, read-only**, senza
  connessione PostgreSQL, token o API. Richiede quattro path:
  `OMNIA_RECOVERY_JOURNAL_PATH`,
  `OMNIA_RECOVERY_CHECKPOINT_PATH`,
  `OMNIA_RECOVERY_JOURNAL_KEY_PATH`,
  `OMNIA_RECOVERY_CHECKPOINT_KEY_PATH`. Exit 0 solo per audit
  riuscito, 2 per errore; stdout espone solo il numero aggregato.
- Test xUnit e CI RED/GREEN comprendono journal valido, due attori
  sintetici, troncamento e rollback del solo checkpoint, gap,
  duplicate UUID/proprietà, riordino, MAC forgiati, limiti e
  file insicuri. La CI compila la CLI e ne prova il rifiuto senza input.

## Confini e obblighi futuri

**Rilevare il troncamento presuppone un checkpoint più recente, integro
e amministrato da un'autorità esterna non ripristinata e non
controllabile da chi modifica il journal.** Se l'attaccante può
riportare indietro sia il journal sia il checkpoint o possiede
entrambe le chiavi, la verifica non può rilevare l'evento perduto.
Non viene implementata alcuna fonte esterna durabile, append-only,
monotonic counter, custodia HSM/vault o sistema di gestione dell'identità
del richiedente; non si prova che tutti gli eventi di cancellazione
siano stati registrati, o che l'evento fosse legittimo.

Il validatore **NON modifica il DB**, non firma eventi in esercizio,
non applica tombstone, non autorizza la sostituzione di
`Recovery__AccessStateFile` con `ready\n` e non recinta ingress,
repliche, richieste in corso, offline worker o provider. Non
equivale a un secondo recovery staging Azure o al soddisfacimento
del GDPR art. 17. #141, #31 e #109 restano OPEN.

## Riferimenti ufficiali

- RFC 2104 HMAC: https://datatracker.ietf.org/doc/html/rfc2104
- PostgreSQL 17 PITR: https://www.postgresql.org/docs/17/continuous-archiving.html
- GDPR art. 17: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/