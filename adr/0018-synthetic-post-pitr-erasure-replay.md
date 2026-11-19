# ADR 0018 — Riconciliazione delle cancellazioni dopo PITR (solo fixture CI)

**Data:** 2026-11-19  
**Stato:** Accepted per un test di sicurezza sintetico #31/#109/#125; NON è un journal di produzione né un gate di riapertura del traffico implementato.

## Problema

Il restore fisico PITR (ADR 0017) può riportare un database al momento
precedente a una cancellazione valida. La nota rimossa dopo il restore
point riappare nel cluster recuperato. Recuperabilità e diritto alla
cancellazione sono due obblighi operativi differenti: un backend di
produzione non dovrebbe riaprire il traffico fino a quando gli eventi
di erasure successivi al punto di recovery siano riconciliati.

## Decisione — solo prova in PostgreSQL temporaneo

- La prova PITR CI prepara una **seconda identità sintetica** prima del
  basebackup, mediante il normale consenso e write path cifrato; serve
  come test negativo contro cancellazioni cross-tenant.
- Dopo aver eliminato la nota sintetica sul **sorgente**, il test crea
  in una directory privata del runner, fuori dal backup/PGDATA/WAL,
  una ricevuta JSON v1 con operazione `erase-actor`, restore point
  nominato e identificatori di fixture. Una chiave HMAC-SHA256 casuale
  di 32 byte firma l'esatta sequenza di byte della ricevuta; la
  firma e la chiave risiedono nello stesso ambito effimero privato.
- L'harness `Omnia.RestoreRehearsal` accetta esclusivamente la fixture
  nota e un database di prova PostgreSQL sul loopback. Valida schema,
  versione, proprietà esatte senza duplicati, ID e restore point.
  Confronta il MAC in tempo costante prima di qualsiasi cancellazione.
  Nessun selettore utente proveniente dal JSON può autorizzare
  cancellazioni arbitrarie: la destinazione è hard-coded di test.
- Il test verifica che ricevuta assente o MAC alterato siano rifiutati
  senza mutare il database recuperato. In seguito al MAC valido,
  `PostgresPersonalDataService.EraseAsync` elimina memo, vettore e
  consenso tramite la scope RLS e l'actor lock preesistenti.
  Un secondo replay è idempotente e l'export dell'attore deve essere
  vuoto. La nota del tenant sintetico indipendente resta leggibile.
- La CI non espone endpoint, non consente accesso pubblico al clone
  durante il recovery e distrugge backup, WAL, ricevute e chiavi.

## Limiti espliciti e rischi ancora aperti

Questa prova **non implementa** il journal esterno di produzione.
La ricevuta e il MAC sono generati nello stesso runner dopo il DELETE;
mancano una persistenza transazionale esterna, watermark/sequence monotona
e anti-rollback, prova di *completezza* del journal, custodia delle chiavi
da HSM/vault, firme da più dispositivi, audit indipendente e policy di
conservazione per identificatori. Verificare un MAC non dimostra che
nessuna cancellazione sia stata omessa dal file o che il sorgente della
ricevuta fosse davvero autorizzato. Inoltre non esiste ancora un
gate applicativo/di rete che impedisca l'accesso al cluster restaurato
prima della riconciliazione verificata, né propagazione agli object
store, provider, cache, indici o backup storici.

**Non usare** questo harness per dati personali reali: il suo signer è
una utility di test, non una procedura organizzativa GDPR. Il diritto
all'oblio e le sue eccezioni vanno analizzati legalmente, senza
presumere automaticamente che un backup debba essere riscritto.
Restano OPEN #31/#32/#109/#125/#140 e la parent privacy #2.

## Riferimenti

- PostgreSQL 17 PITR: https://www.postgresql.org/docs/17/continuous-archiving.html
- Regolamento (UE) 2016/679, art. 17: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
- ICO right to erasure and backups: https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/individual-rights/individual-rights/right-to-erasure/