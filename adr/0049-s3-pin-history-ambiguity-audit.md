# ADR 0049 — S3 WORM pin history: diagnostica di ambiguità, NON authority anti-rollback

**Data:** 2026-11-21  
**Stato:** Accepted per slice sintetica di osservazione read-only, non production-ready.  
**Issue:** #150, parent #144/#141, follow-up ADR 0048/#149.

## Problema

Una versione S3 Object Lock COMPLIANCE custodisce i byte della *singola versione*.
Un delete marker può rendere diverso `latest` senza eliminare la versione
trattenuta; dopo il marker può essere caricato un secondo oggetto (anche
COMPLIANCE) sotto la stessa key. Il pin HMAC del primo resta autentico,
ma né `If-None-Match: *` né un solo `VersionId` attestano una
sequenza globale «once-ever». Confondere osservazione e unicità
è particolarmente pericoloso dopo doppio PITR PostgreSQL.

## Decisione

Aggiungere `S3ObjectLockPinHistoryAudit`, privo di operazioni write,
che riusa le porte `IS3AttemptVersionHistory` e
`S3ObjectLockVersionPinService.VerifyPinnedAsync`.

- Richiede **dall'esterno** entrambi i VersionId e tre chiavi HMAC
  diverse; nessun lookup a latest e nessuna identificazione attore
  da un database ripristinato.
- Usa `ListVersions` dell'adapter AWS SDK, max 2 entries/pagina,
  max 8 pagine, valuta l'intero catalogo visibile prima del
  readback HMAC+COMPLIANCE dei due oggetti.
- **Uncertain** se delete marker, VersionId differente o duplicato,
  chiave estranea, pagination troncata/ciclica, 403, timeout,
  errore di schema, versione unsafe, reader non valido o ACL negata.
  La prova negativa non significa «nessun tentativo».
- `SingleVisiblePinVerified` indica solo una singola versione
  **visibile nella risposta del provider**, firmata e verificata
  assieme alla ricevuta referenziata. In ogni esito:
  `MayAutoRetry=false`, `MayReleaseRecovery=false`,
  `ProvesCompleteHistory=false`, `ProvesSourceDeleteCommit=false`.
- Nessun uso nell'API, nel DELETE, nei worker, nella generazione
  di `ready` o nel replay.

## Test, limiti e decisioni aperte

TDD test-first in `tests/Omnia.S3ObjectLock.Tests/S3ObjectLockPinHistoryAuditTests.cs`:
due bucket Object Lock su SeaweedFS sintetico, baseline di un solo pin,
inserimento di delete marker e successivo secondo pin COMPLIANCE; la
versione precedente resta verificabile esattamente, ma l'audit deve
segnalare l'ambiguità. Fixture di malformed/foreign/truncated history
devono evitare qualsiasi readback.

**Nessuna prova di completezza:** i metadata di `ListVersions`
sono sotto lo stesso amministratore, non sono un checkpoint
indipendente, non sopravvivono necessariamente a failure/rollback del
provider, e l'audit non prova che la lista ricevuta sia esaustiva.
Un ledger immutabile fuori banda e monotono, identità/IAM separate,
source DELETE COMMIT, prova end-to-end Azure/on-prem e GDPR review
restano oggetto delle P0 #144/#141/#109/#31, tutte APERTE.

### Evidenza CI

RED: commit test-first `5a61e10a`, workflow [GitHub Actions](https://github.com/KeyserDSoze/Omnia/actions/runs/44905825570); atteso CS0246 per nuovo tipo assente (verificare esito prima di registrare failure effettiva). GREEN e run finale vanno aggiunti soltanto dopo verifica Actions.

## Fonti

- [Amazon S3 Object Lock — versioning e delete marker](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html)
- [ListObjectVersions](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectVersions.html)
- [Regolamento UE GDPR 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
