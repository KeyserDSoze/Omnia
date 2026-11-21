# ADR 0047 — Riconciliare ACK S3 perso mediante catalogo delle versioni, senza attestare assenza

**Data:** 2026-11-21
**Stato:** Accepted soltanto per *osservazione positiva read-only* su protocollo S3 sintetico in CI; **NON registrazione monotona offsite**.
**Issue child:** #148; parent P0 #144 e #141.
**Dipendenze:** ADR 0043 (HMAC receipt), ADR 0044 (S3 version-pinned reader), ADR 0046 (PUT conditional, `Uncertain` senza retry).

## Problema

Una PUT condizionale COMPLIANCE può terminare realmente sul server anche se
il writer perde la risposta (timeout/reset fra COMMIT remoto e ACK). Il
publisher ADR0046 deve dire `Uncertain`, ma non dispone più del
`VersionId` necessario per confermare quella versione specifica.
Un `GetObject` senza `VersionId` recupererebbe la versione *latest*
mutabile, che potrebbe essere stata sovrascritta o occultata da un
*delete marker* senza eliminare la versione protetta. Peggio,
un `ListVersions` vuoto o incompleto non certifica che non vi sia stato
alcun tentativo: permessi, rollback amministrativo, replica, paginazione
o omissioni possono nascondere precedenti versioni.

## Decisione

Nuova porta `IS3AttemptVersionHistory`, adapter
`AwsS3AttemptVersionHistory` basato su
`IAmazonS3.ListVersionsAsync` con `Prefix` uguale alla **chiave
canonica completa**, `MaxKeys=2` e paginazione per
`NextKeyMarker` e `NextVersionIdMarker`. L'operazione richiede il
permesso `s3:ListBucketVersions`, diverso da `s3:ListBucket`, oltre
ai permessi di lettura di versioni e retention.

Il servizio `S3ObjectLockAttemptHistoryReconciler`:

1. Rifiuta input malformati, GUID vuoto, clock non UTC, MAC keys non
   distinte, store identity non configurata e alias unsafe
   `null`/`latest`.
2. Esegue **solo operazioni di lettura** del catalogo, con limite
   massimo di **8 pagine da 2 entry** (16 versioni) e controlli
   contro record estranei al key, version ID duplicate, cursori
   mancanti/ripetuti e pagine incoerenti. Scansiona tutte le
   pagine **prima** di interrogare una versione candidata.
   `DeleteMarker` viene riconosciuto come metadata di
   catalogo e non è letto come contenuto/receipt.
3. Solo quando il catalogo presenta una **unica versione
   non-delete-marker**, chiama il reader version-pinned del
   protocollo S3, che deve attestare in COMPLIANCE l'esatto
   `VersionId`, retention futura, store identity, UUID
   e HMAC scope/evidence per tenant e actor.
4. Restituisce `PreviouslyObserved(versionId)` soltanto per
   l'osservazione positiva verificata. Se nessuna versione
   esiste, se vi sono versioni multiple o il catalogo/reader
   risponde 403/404/5xx, errori XML, truncate oltre limite,
   versione foreign, scope invalido, lock Governance o
   retention scaduta, restituisce `Uncertain` con
   `VersionId=null` e MAI una attestazione di assenza.
5. In entrambi i casi `MayAutoRetry=false`,
   `MayReleaseRecovery=false` e
   `ProvesSourceDeleteCommit=false`. Nessuna PUT/DELETE,
   nessun nuovo claim UUID, nessuna integrazione API/worker,
   nessuna emissione di `ready`.

Il limite stretto privilegia un **falso incerto** (da investigare
manual­mente) rispetto a una conclusione errata. Il catalogo può
cambiare durante la scansione, quindi anche un'osservazione positiva
non prova che tutte le versioni storiche siano note; non è un pin
immutabile, né un permesso per proseguire.

## Test e vincoli

Nel container temporaneo CI SeaweedFS (lo stesso GitHub runner)
l'AWS SDK reale esegue una singola PUT COMPLIANCE e il wrapper del
test **scarta appositamente l'ACK dopo che il server ha risposto
positivamente**: il publisher conclude `Uncertain`, mentre una
riconciliazione successiva osserva la versione effettiva senza
ripetere PUT o toccare il PostgreSQL primario. Un secondo scenario
inserisce un delete marker e verifica che l'evidenza precedente
rimanga osservabile tramite la versione storica. Sono previsti
test con chiave/soggetto straniero, nessun oggetto e due versioni
S3 valide che risultano *ambigue*. Stubs controllati iniettano
cursore ripetuto, mancanti e record foreign: il reader non
deve essere chiamato su un indice inaffidabile.

Il test NON dimostra che storage e amministrazione siano
separati dal cluster PostgreSQL: SeaweedFS è sullo stesso
runner e un amministratore potrebbe distruggerlo. Un ruolo
S3 senza `ListBucketVersions` deve restare `Uncertain`;
l'assenza in un catalogo non è un risultato autorizzativo.
Inoltre non prova monotonicità, disponibilità in caso di
PITR completo/rollover di tutte le authority, durata retention
approvata, protezione di dati personali, source DELETE COMMIT
o legal compliance. La negative physical dual-claim PITR
continua a mostrare il replay non sicuro e deve rimanere
quarantinata.

## Esiti futuri richiesti per chiudere P0 #144/#141

Serve un *external pinned/versioned checkpoint* con numero
monotono e trust domain amministrativamente indipendente,
account/bucket owner e credenziali vincolate, WORM S3 su
reale host offsite e Azure Blob WORM reale in staging, error
injection distribuita/power loss, firma e tracciamento della
prova post-COMMIT del DELETE primario, verifica global
ingress/worker/replica e autorizzazione umana documentata;
la presente slice non rilascia nessuno di quei gate.

## Fonti ufficiali

- [AWS S3 ListObjectVersions](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectVersions.html) — elenco versioni incluse quelle storiche, permesso ListBucketVersions, paginazione e 200 con XML non attendibile.
- [AWS SDK .NET v4 ListVersionsAsync](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/MIS3ListVersionsAsyncListVersionsRequestCancellationToken.html).
- [AWS SDK .NET v4 S3ObjectVersion](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TS3ObjectVersion.html).
- [AWS — Managing delete markers](https://docs.aws.amazon.com/AmazonS3/latest/userguide/ManagingDelMarkers.html).
- [AWS — S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html).
- [GDPR (UE) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/).

## TDD

**RED test-first verificata:** [commit 5b6aa349](https://github.com/KeyserDSoze/Omnia/commit/5b6aa349bab58277b4f3801b1a4b88230bc6c981), [CI 44896553267](https://github.com/KeyserDSoze/Omnia/actions/runs/44896553267) FAILURE prevista: errori CS0246 per porta `IS3AttemptVersionHistory` e modello `S3AttemptHistoryPage` inesistenti prima dell'implementazione.

**GREEN e2e sul commit documentale `d218100f`:**
[CI 44896756880](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756880) **SUCCESS**: 10/10 S3 SDK/SeaweedFS (3 nuovi lost ACK/marker/ambiguous history), 4/4 Azure SDK HTTP sintetico, 19/19 Domain e 80/80 PostgreSQL Integration, PITR fisici, Docker/mobile. [OIDC E2E 44896756790](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756790) e [Pages 44896756867](https://github.com/KeyserDSoze/Omnia/actions/runs/44896756867) **SUCCESS** sul medesimo SHA. La negative rehearsal simultanea genera tuttora `PHYSICAL_DUAL_CLAIM_PITR_UNSAFE_REPLAY_OBSERVED` e `PHYSICAL_DUAL_CLAIM_PITR_REMAINED_QUARANTINED`. La suite non ha accesso a storage realmente offsite né a pin monotono.

Le Actions di ogni eventuale commit successivo devono essere
nuovamente verificate prima della chiusura #148. **#144,
#141, #109 e #31 rimangono aperte**.
