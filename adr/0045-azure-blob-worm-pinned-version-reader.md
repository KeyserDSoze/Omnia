# ADR 0045 — Reader Azure Blob WORM, versione pinned e policy Locked

**Data:** 2026-11-21  
**Stato:** Accepted come adapter read-only con Azure SDK HTTP contract test, **NON Azure WORM production-tested**.  
**Issue:** #146 child di #144 / #141.  
**Precedenti:** ADR 0043 (contratto immutabilità), ADR 0044 (S3 Object Lock).

## Decisione

L'Azure SDK .NET Blob Storage supporta una lettura di una esatta versione
tramite `BlobClient.WithVersion(versionId)`, e la GET delle proprietà della
stessa versione può riportare una `ImmutabilityPolicy` con
`PolicyMode == Locked` e `ExpiresOn`. Semplicemente abilitare versioning
o il *supporto* per version-level WORM sull'account NON rende immutable
le singole versioni: la policy effettiva va verificata per ciascuna.

Il componente `Omnia.ObjectStore.AzureBlob.AzureBlobWormAttemptReader`
è un adapter READ-ONLY del port `IImmutableAttemptObjectReader`, separato
dal backend REST e dal dominio. L'Azure SDK è una dipendenza esclusiva
del progetto provider. Richiede l'identità dello storage configurata
dall'esterno, un BlobContainerClient HTTPS già autenticato e un
object key deterministico `erasure-attempts/v1/{uuid:N}.json`.

Per prevenire downgrade involontari al blob *latest*, la versione
deve essere esplicita, un timestamp Azure canonico UTC (frazione
7 cifre, suffisso Z). Si istanzia il client versioned una volta,
poi si interrogano:

1. `GetPropertiesAsync`: la proprietà `VersionId` deve corrispondere
   esattamente al pin; la `ImmutabilityPolicy` di **questa versione**
   deve essere `Locked`, con `ExpiresOn` UTC e oggetto tra 2 e
   512 byte. `Unlocked`, policy assente, versione differente e
   dimensioni incongrue si rifiutano.
2. `DownloadStreamingAsync` del **medesimo client versioned**:
   `VersionId`, `ContentLength` ed ETag devono confermare le
   proprietà precedenti. Anche se un server mente su ContentLength,
   la lettura streaming ha buffer di 513 byte (512+1) per
   rifiutare payload troppo grandi. Nessuna copia su file.
3. Il verificatore ADR0043 applica le **due HMAC distinte**
   e verifica UUID/tenant/soggetto, metadata, version ID e
   la scadenza rispetto a un clock UTC esplicito. Il solo
   adapter non possiede API di Put, Lock, Delete, claim o
   sblocco della quarantena.

## Prove e limiti (non produzione)

`tests/Omnia.AzureBlobWorm.Tests` usa il vero
`Azure.Storage.Blobs` .NET SDK e un transport HTTP sostitutivo
interno al processo xUnit. Le risposte HEAD e GET sono sintetiche,
non emesse da Azure; ogni richiesta deve trasportare lo STESSO
query parameter `versionid`. Sono esercitati esiti Locked,
Unlocked, no policy, expired, HTTP 403/404, versione diversa,
payload tampered/overlimit, foreign scope/store/key e alias
`null`/`latest`. La suite verifica anche che un risultato
non possa attestare un DELETE COMMIT, riemettere un tentativo
o produrre `ready`.

Azurite NON è fonte di verità per la policy Azure WORM reale.
Questa slice **non** esegue policy locked su account Azure con
version-level immutability, non testa Managed Identity, RBAC,
control-plane governance, WORM deletion denial, segregazione
amministrativa, Az Key Vault, serializzazione di un primary
COMMIT, stamp, time authority o pin monotono offsite. Tutte
queste attività rimangono in #144 e #141. Il `storeIdentity`
è un input configurato, non una prova cryptographic dell'account.

Anche i MAC e UUID sono dati pseudonimizzati, non anonimi.
Policy di retention e base legale GDPR vanno documentate per
qualsiasi installazione reale e non discendono automaticamente
dall'esito dei test.

## Fonti ufficiali

- [Microsoft — Version-level WORM policies](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies).
- [Microsoft — Configure version-level policies](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-policy-configure-version-scope).
- [Microsoft — BlobClient.WithVersion](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.blobclient.withversion).
- [Microsoft — BlobBaseClient.GetPropertiesAsync](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.specialized.blobbaseclient.getpropertiesasync).
- [Microsoft — Get Blob Properties REST](https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob-properties).
- [Microsoft — Azure.Storage.Blobs SDK](https://www.nuget.org/packages/Azure.Storage.Blobs/12.30.2).
- [GDPR (UE) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/).

## Verifiche TDD

La prova RED e GREEN completa si registra sulle issue solo
dopo aver verificato i run effettivi GitHub Actions sul medesimo
HEAD. Non chiudere #144 o #141 per test con server finto.
