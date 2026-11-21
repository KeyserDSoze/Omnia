# ADR 0054 — Azure WORM signed PIN visible-history forensic audit (NOT monotonic proof)

**Data:** 2026-11-21  
**Stato:** Accepted only as **read-only** Azure SDK scripted-protocol diagnostic; not a production offsite or anti-rollback authority.  
**Issue:** #155 child of #144/#141; follows #154/ADR0053, #152/ADR0051, S3 #150/ADR0049.

## Problema

Una versione PIN AzureLocked è autenticata da tre HMAC domain-separated e
rimanda alla versione esatta della prova originale. La verifica esatta
`AzureBlobVersionPinService.VerifyPinnedAsync` **non** esamina se
esistano altri PIN, snapshot, tombstone o una versione diversa divenuta
`latest` dopo un DELETE. Come per S3 #150, la persistenza della
prima versione non dimostra che la chiave non sia stata riutilizzata
in altri momenti. La lista del provider può inoltre essere incompleta
o manipolata dall'amministratore: un risultato "clean" non è una prova
monotona né una fonte di truth indipendente.

## Decisione

Implementare `AzureBlobWormPinHistoryAudit`, un componente
**solo read-only** che accetta `IAzureBlobVersionHistory` e
`AzureBlobVersionPinService`. Il service di verifica contiene un writer
per il separato caso PublishOnce, ma questo audit chiama ESCLUSIVAMENTE
`VerifyPinnedAsync`, mai `PublishOnceAsync` e non ottiene
alcun diritto di mutazione o replay.

Per una key deterministica `erasure-pins/v1/{requestId:N}.json`
il chiamante fornisce entrambi gli esatti VersionId Azure UTC
(original evidence + signed pin), UUID, scope attore, tre chiavi
HMAC distinte e un clock UTC. Validazioni di sicurezza sui parametri
avvengono **prima** del contatto al provider.

Riusare `AzureBlobSdkVersionHistory` aggiornato per accettare
**soltanto** le chiavi canoniche dei namespace original evidence
e signed PIN. Il vero SDK Azure.Storage.Blobs continua a richiedere
`Version | Deleted | DeletedWithVersions | Snapshots`, con prefisso
exact-key e pagine da 2 (max 8, continuazioni bounded e non ripetibili).
La lista non permette `latest` GET, wildcard o prefix generici.

Se elenco vuoto, duplicato, competitor, versione null/latest/non UTC,
key estranea, noncurrent version, deleted-with-versions, soft-deleted,
snapshot, pagina troppo grande o vuota dopo continuazione,
cursor mancante/ciclico/invalido, 403/timeout o qualsiasi
errore del provider => **`Uncertain`**, niente GET, nulla è
considerato "mai scritto". Solo un PIN versione visibile uguale
all'expected, `IsCurrentVersion=true` e senza altri metadata
ambigui permette l'autenticazione di **entrambe** le versioni
`AzureLocked` attraverso `VerifyPinnedAsync`.
La scadenza PIN non può essere materialmente anteriore a quella
dell'evidence e i byte devono combaciare con la canonical HMAC envelope.

`AzureBlobPinHistoryAuditOutcome.SingleVisiblePinVerified` è
positivo **solo** per una osservazione circoscritta, non la verifica
di storia globale. In **ogni** risultato:
`MayAutoRetry=false`, `MayReleaseRecovery=false`,
`ProvesCompleteHistory=false`,
`ProvesMonotonicHistory=false`,
`ProvesSourceDeleteCommit=false`.
Nessuna integrazione API, worker, DELETE o recovery-ready.

## Test e TDD

Test-first `tests/Omnia.AzureBlobWorm.Tests/AzureBlobWormPinHistoryAuditTests.cs`
copre una unica PIN valida Locked/HMAC in due store configurati,
versione concorrente, snapshot, deleted/noncurrent, alias VersionId,
key straniera, page cap/loop/truncation/403, actor/key/clock invalidi,
pin MAC alterato, version/store/expiry errati e garanzia ZERO calls
al writer. Il vero SDK Azure è interrogato tramite responder HTTP
sintetico che verifica `include=versions,deleted,deletedwithversions,snapshots`
e `prefix=` key PIN esatta. Non è stata chiamata Azure live.

- RED test-first commit `b7a9de3f`:
  [GitHub CI 44922335677](https://github.com/KeyserDSoze/Omnia/actions/runs/44922335677).
  Verificare l'esito prima di attestare RED avvenuto.
- Implementazione `5b2770d1` e estensione strict-key
  `1384ab66`:
  [CI 44922394452](https://github.com/KeyserDSoze/Omnia/actions/runs/44922394452).
  Attendere e registrare prova GREEN.
- Prima di chiudere #155, confermare **CI, OIDC real-browser
  e Pages SUCCESS sullo stesso SHA finale** e includere i link
  in issue. Eventuali nuove modifiche producono un nuovo HEAD
  da verificare separatamente.

## Rischi residui e GDPR

Un PIN visibile unico non dimostra l'assenza di versioni omesse
o di un precedente COMMIT, né la separazione amministrativa dei
due container, la custodìa offsite, l'autenticazione live dei provider,
il rollback simultaneo di più PGDATA, un authority monotono globale,
la prova del DELETE del primario o la certezza di
esecuzione della cancellazione GDPR. I PIN HMAC+UUID restano
potenzialmente dati personali pseudonimizzati, da conservare
solo con finalità/retention giustificata e valutazione normativa.
Non rilasciare la quarantena dopo PITR o dati personali veri.
Mantenere **#144/#141/#109/#31 OPEN**.

## Fonti ufficiali

- [Azure List Blobs REST](https://learn.microsoft.com/en-us/rest/api/storageservices/list-blobs)
- [Azure BlobStates flags](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.models.blobstates)
- [Azure WORM per-version](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies)
- [GDPR 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
