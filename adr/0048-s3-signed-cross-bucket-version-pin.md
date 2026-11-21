# ADR 0048 — S3 signed exact-version pin su bucket WORM separato, senza autorità monotona

**Data:** 2026-11-21  
**Stato:** Accepted come slice protocol/cryptographic CI sintetica **NON indipendente**, **NON production-ready**.  
**Issue child:** #149 (parent #144/#141).  
**Precedenti:** [ADR 0043](0043-immutable-attempt-version-pinned-contract.md), [ADR 0044](0044-s3-object-lock-version-pinned-provider-reader.md), [ADR 0046](0046-s3-conditional-put-uncertain-ack.md), [ADR 0047](0047-s3-history-reconcile-lost-put-ack.md).

## Problema

In ADR 0047 si possono enumerare versioni S3 in presenza di un ACK di
PUT perso, ma un catalogo oggetti non è una storia monotona attestata
fuori dai database: l'admin può cambiare la versione corrente tramite
PUT/delete marker, rendere il bucket indisponibile o rimuovere la replica.
Il `VersionId` verificato di un tentativo va associato a una ricevuta
separata autenticata, pronta in futuro per essere custodita da un altro
trust domain. Un secondo bucket dello **stesso provider e con lo stesso
amministratore**, com'è oggi in CI, non realizza quell'indipendenza.

Come documenta AWS, Object Lock COMPLIANCE protegge la specifica
*versione* e non impedisce un `DELETE` privo di VersionId che crea un
delete marker, né impedisce nuove versioni dopo tale marker. Il
`If-None-Match: *` sul PUT non costituisce un vincolo assoluto
"once-ever" né un contatore monotono globale.

## Decisione (slice #149)

- `S3ObjectLockVersionPinService` riceve due adapter read-only,
  uno per l'evidenza originale ADR0044, uno per il namespace di pin,
  e una porta `IS3ObjectLockAttemptPut` sul bucket pin. Il
  `S3ObjectLockPinReader` usa l'AWS SDK .NET v4 reale su
  `GetObjectRetention` + `GetObject` con lo **stesso identico**
  `VersionId`, richiede COMPLIANCE, UTC e payload limitato a
  1024+1 byte, esclude `null/latest` e chiavi fuori dal
  namespace `erasure-pins/v1/<requestId:N>.json`. L'identità
  dello store è una configurazione, **non** una prova indipendente
  di account/bucket ownership.
- Il payload pin è JSON **byte-canonico**, versione 1,
  purpose `omnia-s3-attempt-version-pin-v1`, UUID,
  `evidenceStoreIdentity`, `evidenceVersionId`,
  `scopeMac`, `mac`. Non contiene tenant/owner plaintext.
  Lo `scopeMac` è HMAC-SHA256 dello scope attore (UUID,
  tenant, soggetto) con purpose distinto
  `omnia-s3-pin-actor-scope-v1`; `mac` è un **secondo**
  HMAC-SHA256 della forma canonica senza `mac`, firmata con
  una terza chiave dedicata. Le chiavi da 32 byte
  **scope/evidence/pin** sono distinte per contenuto.
  La received evidence viene autenticata separatamente
  tramite i due MAC del contratto ADR0043.
- `PublishOnceAsync` **prima** verifica l'esatta versione
  della ricevuta originale con `ImmutableAttemptEvidence.VerifyAsync`
  e controlla il suo S3 COMPLIANCE con retention futura.
  Poi serializza il pin e invoca una sola PUT sul bucket
  del pin, modalità COMPLIANCE e `If-None-Match: *`
  con SDK `MaxErrorRetry=0`, usando come `retainUntil`
  la scadenza osservata della ricevuta originale.
  Successivamente legge di nuovo il pin per
  `VersionId` restituito e riverifica **entrambi** gli
  oggetti; il pin non deve scadere prima della
  ricevuta originale (tolleranza inferiore a 1 s).
- `VerifyPinnedAsync` richiede **due version ID espliciti**
  (evidence e pin) che un sistema esterno dovrà fornire;
  non usa PostgreSQL, `latest`, né `ListVersions` come
  fonte attendibile del pin stesso. Confronta
  l'oggetto pin con la forma canonica attesa, in
  tempo costante, verifica HMAC e scope e conferma
  la ricevuta originale per il VersionId esatto.
  In caso di mismatch di owner/MAC, VersionId,
  store identity, governance, retention, dati o
  timeout ritorna `Uncertain`, non una
  falsa attestazione di assenza.
- Il risultato `S3AttemptVersionPinOutcome`
  può essere `PinObserved` **solo** per la prova
  positiva di due versioni concrete;
  `MayAutoRetry=false`, `MayReleaseRecovery=false`,
  `ProvesSourceDeleteCommit=false`,
  `ProvesMonotonicHistory=false` in ogni caso.
  Nessuna DI in API, DELETE, worker, replay,
  issuance di ready o traffico sbloccato.

## Test reali CI e minacce non risolte

Una nuova suite xUnit usa **due bucket S3 Object Lock distinti**
in SeaweedFS, sullo **stesso runner GitHub con identità admin
comune**: PUT synthetic evidence, PUT pin COMPLIANCE e
GET per due VersionId. Verifica il blocco 403 della
DELETE esatta pin prima della retention, il mantenimento
del pin storico dopo delete marker latest e il rifiuto
di una successiva versione GOVERNANCE. Matrice
negativa per tenant/soggetto, key, versione evidenza
o pin, versione `null/latest`, MAC key differente.
Wrapper fault injection perde intenzionalmente
l'ACK **dopo** la PUT pin accettata; l'operazione
originale resta `Uncertain` e non fa retry,
anche se un controllo successivo con VersionId
**simulato come fornito esternamente** può trovare
l'oggetto positivo.

**TDD RED test-first**: [commit 009bd742](https://github.com/KeyserDSoze/Omnia/commit/009bd7429c8fecc9ee4db3e4a38da857eb6829b5), [CI 44904227083](https://github.com/KeyserDSoze/Omnia/actions/runs/44904227083) **FAILURE attesa** CS0246 perché `S3ObjectLockPinReader` e `S3ObjectLockVersionPinService` non esistevano.
La prima compilazione del provider `e255d885` ha evidenziato che `PreEraseExecutionClaimScope` è correttamente interno all'Infrastructure; la correzione `5c9b7d88` ha creato un HMAC actor-scope con purpose separato, senza ampliare la visibilità dell'helper interno.
[CI 44904518296](https://github.com/KeyserDSoze/Omnia/actions/runs/44904518296) sul commit `5c9b7d88` **SUCCESS**: 13/13 S3, 4/4 Azure SDK HTTP, 19/19 Domain, 80/80 PostgreSQL Integration, PITR/Docker/mobile.

**GREEN hardening** dopo test addizionale di expiry pin abbreviata,
identity store foreign, chiave HMAC ripetuta e provider mode AzureLocked
sul [commit 3db924a0](https://github.com/KeyserDSoze/Omnia/commit/3db924a080f68f2bf654854cb80a79cbc7286421):
[CI 44904790204](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790204) **SUCCESS** con **14/14 S3 Object Lock**, **4/4 Azure SDK HTTP sintetico**, **19/19 Domain**, **80/80 PostgreSQL Integration**, PITR fisici/Docker/mobile.
[OIDC E2E 44904790199](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790199) e [Pages 44904790212](https://github.com/KeyserDSoze/Omnia/actions/runs/44904790212) **SUCCESS**, stesso SHA.
La negative rehearsal dual-PGDATA ha ancora
`PHYSICAL_DUAL_CLAIM_PITR_UNSAFE_REPLAY_OBSERVED` e
`PHYSICAL_DUAL_CLAIM_PITR_REMAINED_QUARANTINED`.
Nessun test dà la completezza di un monotonic checkpoint offsite.

Per le eventuali modifiche successive a questo commit bisogna
verificare nuovamente **tutte** le Actions finali su main
prima di chiudere #149. #144/#141/#109/#31 sempre OPEN.

Questo esperimento NON certifica offsite,
segregazione amministrativa o IAM/Managed Identity,
durabilità su host indipendente, vero audit WORM
di Azure o S3 prod, orologio trusted, gestione
vault/HSM e rotazione delle chiavi, protezione
contro perdita simultanea del bucket evidenza
e bucket pin, monotonicità tra tutte le richieste,
completezza del catalogo, idempotenza dopo doppio
PITR PostgreSQL o DELETE primario confermato.
`PinObserved` non è prova di erasure né una
autorizzazione a riaprire i dati ripristinati.
È un **mattoncino** per l'autorità esterna richiesta
dalle issue P0 #144 e #141, entrambe da mantenere aperte.

Gli identificatori UUID e gli HMAC sono
*pseudonimizzati*, non automaticamente anonimi:
la durata della retention del pin deve essere
definita con finalità/base giuridica GDPR e con
cancellazione a scadenza, includendo review
documentata Art. 5, 17, 25 e 32.

## Fonti

- [Amazon S3 Object Lock e delete marker](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html).
- [PutObjectRequest IfNoneMatch e ObjectLockMode, SDK .NET v4](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html).
- [S3 GetObjectRetention](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectRetention.html).
- [S3 GetObject with VersionId](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html).
- [Regolamento (UE) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/).
