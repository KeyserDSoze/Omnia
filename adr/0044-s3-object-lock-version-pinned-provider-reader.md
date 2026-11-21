# ADR 0044 — Adapter S3 Object Lock version-pinned: reale GET/retention, non offsite

**Data:** 2026-11-21  
**Stato:** Accepted limitatamente ad adapter read-only e protocol test CI sintetico; **NON production-ready**.  
**Issue child:** #145 (S3), parent #144 e #141 P0.  
**Precedente:** [ADR 0043](0043-immutable-attempt-version-pinned-contract.md).

## Motivazione

Dopo ADR 0043 non vi era nessun adapter di provider che interrogasse
un backend S3 tramite le sue API effettive. Un reader in-memory può
affermare `S3Compliance` senza aver verificato una policy di retention.
L'obiettivo qui è testare **protocollo realmente versionato** e
`GetObjectRetention` di S3 su un servizio OSS temporaneo, senza
confondere questo risultato con indipendenza di custodia.

L'immagine ufficiale MinIO Community non è una base stabile a partire
dal 2026: le immagini storiche sono state rimosse dai registri e il
progetto community è archiviato. Non introdurre un'applicazione dipendente
da un mirror Docker terzo e non assumere MinIO produzione. Un S3
compatibile mantenuto, **SeaweedFS**, è usato esclusivamente per test CI
di compatibilità S3 Object Lock. Questo non è un impegno architetturale
irreversibile: gli adapter usano le API ufficiali S3 e possono essere
testati su provider S3 equivalenti con supporto reale Object Lock.

## Implementazione

- Progetto separato `src/backend/Omnia.ObjectStore.S3` con AWS SDK
  for .NET v4, versione NuGet bloccata, *senza* nuove dipendenze cloud
  nel Domain/Application/Infrastructure core o nell'API runtime.
- `S3ObjectLockAttemptReader : IImmutableAttemptObjectReader` riceve
  un `IAmazonS3` già autenticato e un bucket/store identity configurati;
  non crea credenziali, scrive o elimina oggetti, gestisce traffico, emette
  token di release o effettua un nuovo claim.
- Richiede `erasure-attempts/v1/<uuid-32-lowercase>.json` e
  `VersionId` esplicito, non `latest` e mai S3 `null`.
  Rifiuta chiavi fuori namespace e token di versione non validi.
- Interroga `GetObjectRetention(BucketName,Key,VersionId)`:
  HTTP 200, modalità **COMPLIANCE** e scadenza UTC; GOVERNANCE,
  oggetto senza lock o assenza privilegi `s3:GetObjectRetention`
  devono negare.
- Interroga `GetObject(BucketName,Key,VersionId)`, controlla HTTP
  200, response VersionId esattamente uguale al pin, content length
  positiva e non superiore a 512, streaming bounded (max 513 byte)
  per rifiutare servizi che mentono sulla dimensione. Errori S3/I-O
  falliscono chiusi. Rimane compito del layer ADR0043 autenticare
  HMAC, tenant, soggetto, purpose, store identity e retention non scaduta.
- Il controllo della stringa `storeIdentity` è una configurazione del
  reader e **non** attesta cryptograficamente endpoint, proprietario
  account, repliche, ruolo SQL o posizione dei dati. Per AWS reale si
  dovrà vincolare l'account atteso (`ExpectedBucketOwner`), per
  S3 compatibile servono identità di controllo esterno.
- Nessuna API di put/del è presente nel reader. Il test-only
  bootstrap CI usa credenziali **amministrative sintetiche**: il
  contratto produttivo richiede permessi GET-version/GET-retention,
  mai PutObjectRetention, Governance bypass o delete.

## Test reali del protocollo

`tests/Omnia.S3ObjectLock.Tests` invoca realmente l'AWS SDK e un
container SeaweedFS S3 con segreti casuali su loopback, caricato
temporaneamente da GitHub Actions, e bucket Object Lock-enabled
versionato. Il test effettua:

1. PUT sintetico con modalità COMPLIANCE e retain-until futura,
   pin di VersionId effettivamente restituito e osservazione
   autenticata dal reader.
2. PUT successivo sullo stesso object key con GOVERNANCE e
   **differente VersionId**: la versione GOVERNANCE è negata,
   ma la precedente COMPLIANCE continua a essere verificabile.
3. DELETE della versione COMPLIANCE prima della scadenza negato
   dal backend, e vecchia versione ancora disponibile.
4. Oggetto privo di retention, versioni `null` e `latest`,
   foreign identity/scope, key fuori namespace e pin inesistente:
   tutti negati senza DELETE primary/API ready.

Questo smoke gira nello **stesso GitHub runner**, con file dati
effimeri e poteri di amministrazione condivisi. Non è separazione
dei failure domain: un amministratore della VM/runner potrebbe
cancellare fisicamente lo storage e aggirare le API. Non prova
né garantisce durabilità, replica, cifratura, IAM produttivo,
WORM legalmente efficace, orologio esterno, vault/HSM, pin
monotono indipendente o attestation del COMMIT del primario.
Non chiudere #144 o #141 per questo adapter.

## ADR e normative / fonti ufficiali

- [Amazon S3 Object Lock, compliance/governance](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html).
- [AWS SDK v4 — GetObjectRetentionAsync](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/MIS3GetObjectRetentionAsyncGetObjectRetentionRequestCancellationToken.html).
- [AWS SDK v4 — GetObject](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TGetObjectRequest.html).
- [AWS SDK v4 — GetObjectResponse.VersionId](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TGetObjectResponse.html).
- [SeaweedFS S3 Object Lock](https://github.com/seaweedfs/seaweedfs/wiki/S3-Object-Lock-and-Retention).
- [SeaweedFS official README](https://github.com/seaweedfs/seaweedfs).
- [MinIO community archived](https://github.com/minio/minio).
- [GDPR UE 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/).

## Evidenza TDD

Registrare run RED e GREEN conclusi e SHA **solo dopo la
verifica delle GitHub Actions**. L'assenza di test Azure
è nota e tracciata in #144; non dichiarare provider parity.
