# ADR 0053 — Azure WORM signed exact-VersionId pin across logical containers (NO independent authority)

**Data:** 2026-11-21  
**Stato:** Accepted only as Azure SDK HTTP-scripted cryptographic/protocol slice; NOT production WORM, offsite or anti-rollback verified.  
**Issue:** #154 child #144/#141, follow-up #146/#151/#152/#153 (ADRs 0045, 0050–0052); Azure counterpart to #149/ADR 0048.

## Problema e decisione

Un precedente `VersionId` Azure Blob verificato con HMAC e policy
`Locked` è ancora vulnerabile a perdita del puntatore durante restore
di PostgreSQL, cancellazione del blob corrente, nuovi PUT sullo stesso
nome e manipolazione del catalogo `List Blobs`. L'admin Azure
può agire su tutti i container che amministra, e un secondo container
con lo stesso admin NON è una fonte indipendente monotona.

Si introduce `AzureBlobVersionPinService` che riceve due reader
`IImmutableAttemptObjectReader`, uno per evidence originale
(max 512 byte) e uno per un pin (max 1024), un writer iniettabile
`IAzureBlobWormPinWrite` e due identità di store **logiche e
differenti**. La distinzione delle stringhe di store non dimostra
RBAC separato, account fisicamente differenti, autenticità di tenant,
accesso segregato, custodia offsite o resistenza a PITR multiplo.

Il pin ha chiave deterministica
`erasure-pins/v1/{requestId:N}.json`, JSON canonico con
schemaVersion=1, purpose=`omnia-azure-attempt-version-pin-v1`,
UUID, evidenceStoreIdentity, **esatto evidenceVersionId UTC**
e `scopeMac` (HMAC su UUID/tenant/subject con purpose
`omnia-azure-pin-actor-scope-v1`) oltre al `mac` separato
HMAC-SHA256 su tutti i campi precedenti con chiave pin dedicata.
Le tre chiavi 32 byte (scope, evidence, pin) sono distinte per
valore e non sono scritte nello storage. Tenant e owner plaintext
non entrano nel JSON. Firma, scope e bytes sono verificati per
uguaglianza costante su forma byte-canonica ricalcolata.

`PublishOnceAsync` verifica PRIMA di qualsiasi scrittura il payload
evidence esatto, l'attore, la policy `AzureLocked`, la sua expiry
futura e due store logici distinti. Poi produce un PIN HMAC e
chiama esattamente **una** PUT condizionale con
`If-None-Match: *`, SDK `Retry.MaxRetries=0`, policy custom
`Unlocked` sull'upload. Dal PUT ACK è obbligatorio un
`VersionId` timestamp UTC canonico; il client usa SOLO
`WithVersion(versionId).SetImmutabilityPolicyAsync(Locked)`,
mai mutable latest. Il pin è osservabile positivo soltanto dopo
HEAD+GET sullo stesso version ID e HMAC, oltre al secondo controllo
dell'evidence originale. La scadenza effettiva del PIN non può
precedere quella originale (tolleranza minuscola di timestamp <1s).

`VerifyPinnedAsync` non scopre mai le versioni: richiede
**entrambi i VersionId** dal caller, che dovranno in futuro essere
conservati da un'autorità amministrativamente distinta da PostgreSQL
e dal blob provider. Non accetta null/latest/alias o malformed UTC.
Gli adapter di PIN mantengono il limite 1024+1, HEAD+GET
stesso VersionId e ETag, `Locked` effettivamente sulla
versione, retention UTC, no policy-inferred from account/container,
no PUT/DELETE nel reader.

Qualsiasi 403/409/412, timeout, perdita di ACK *dopo* il commit
di upload o lock, policy Unlocked, versione mancante/differente,
MAC invalido, store/owner foreign, retention abbreviata o
incomplete readback => `Uncertain` senza possibilità di retry
automatico, DELETE, recovery-ready, replay o deduzione di
assenza. Nessun adapter è registrato nella API o nel worker.

## TDD e prove CI

- RED: `tests/Omnia.AzureBlobWorm.Tests/AzureCrossContainerVersionPinTests.cs`
  test-first [`44721639`](https://github.com/KeyserDSoze/Omnia/commit/4472163900b72794417dd49d3793e2cdbba964c0);
  correzione type-safe del fixture [`d0b83a34`](https://github.com/KeyserDSoze/Omnia/commit/d0b83a3430ec06d6ab78cd5be27532b536b6859c).
  [CI 44920365283](https://github.com/KeyserDSoze/Omnia/actions/runs/44920365283)
  **FAILURE attesa CS0246**: `IAzureBlobWormPinWrite` non ancora implementata.
- Implementazione candidata GREEN [`6a551263`](https://github.com/KeyserDSoze/Omnia/commit/6a55126383d0059546b96935eccf9199af1980f5),
  [CI 44920470848](https://github.com/KeyserDSoze/Omnia/actions/runs/44920470848): verificare il risultato prima di dichiarare GREEN.
- Test con vero client `Azure.Storage.Blobs` e responder HTTP **sintetico**
  per PUT/lock/version-pinned HEAD/GET, 403/lost ACK e
  rifiuto di hidden SDK retry/HTTP. Fake readers attestano
  HMAC actor-bound, MAC tamper, expiry, policy, store, key,
  writer una sola invocazione. **Non** test Azure live.
- Chiusura della child subordinata a SUCCESS delle workflow
  Omnia CI / OIDC real-browser / Pages **sullo stesso HEAD**
  dopo qualsiasi modifica docs/test/code.

## Minacce residue / privacy

Azure documenta che con policy custom la versione può restare
`Unlocked` fino al lock esplicito: la finestra tra PUT e lock
non è atomica. `If-None-Match` non garantisce un
contatore monotono globale; `VersionId` e pin possono
essere persi/omessi dopo rollback simultaneo; due container
nello stesso account e con la stessa identità admin NON
costituiscono custodia indipendente. Mancano provisioning
staging Azure, Managed Identity, RBAC split, auditing degli
amministratori, controprova di DELETE protetta real cloud,
orologio trusted, chiavi KMS/vault e un test offsite
multifailure. Non certifica source DELETE COMMIT,
GDPR erasure o ammissibilità legale della retention:
UUID/HMAC sono pseudonimizzati ma possono restare
dati personali e vanno trattati secondo finalità
e scadenza documentate. **#144/#141/#109/#31 OPEN**.

## Fonti ufficiali

- [Microsoft — version-level WORM, policy custom Unlocked](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies)
- [Azure BlobClient.WithVersion](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.blobclient.withversion)
- [Azure Set Blob Immutability Policy](https://learn.microsoft.com/en-us/rest/api/storageservices/set-blob-immutability-policy)
- [Azure Blob Upload Options](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.models.blobuploadoptions)
- [Regolamento UE 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
