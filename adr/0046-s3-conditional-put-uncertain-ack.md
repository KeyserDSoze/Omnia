# ADR 0046 — S3 Object Lock: PUT condizionale, VersionId pinned, ACK incerto senza retry

**Data:** 2026-11-21  
**Stato:** Accepted come **slice read/PUT sintetica su S3 compatibile in CI**, NON production-ready né offsite custody.  
**Issue:** #147, child #144/#141. **Precedenti:** ADR 0043 (receipt signed), ADR 0044 (reader S3), ADR 0045 (Azure reader).

## Contesto

Prima di questa decisione esisteva un reader reale S3 (GET versione
e GetObjectRetention COMPLIANCE), ma nessun componente che facesse un
PUT condizionale e confermasse che l'esatta versione restituita dal
server conservasse payload firmato e retention attesa. In Object Lock
ogni versione è immutabile durante retention; un successivo PUT sullo
stesso key può comunque generare un'ALTRA versione. `If-None-Match: *`
protegge la chiave **solo finché esiste una versione corrente
non-delete-marker**, non è un contatore monotono né un witness offsite.

La risposta di rete a un PUT può sparire **dopo che il server ha
persistito la versione**. Nessuna assenza di ACK deve essere
interpretata come “non è successo nulla”; il retry automatico
del DELETE primario potrebbe quindi creare tentativi duplicati.

## Decisione e interfacce

- Nuova porta `IS3ObjectLockAttemptPut` e adapter concreto
  `AwsS3ObjectLockAttemptPut` con AWS SDK for .NET v4.
  `AmazonS3Client` deve avere `Config.MaxErrorRetry=0`,
  verificato nel costruttore; un provider che nasconde retry non
  è ammissibile. Né publisher né adapter hanno API DELETE/ready,
  contesto OIDC o attivazione DI nella API.
- Il servizio `S3ObjectLockAttemptPublisher.SubmitOnceAsync`
  richiede **payload canonicale firmato dal chiamante**, scope
  UUID/tenant/owner, due chiavi HMAC indipendenti, retainUntil
  e now UTC espressi esplicitamente. `ImmutableAttemptEvidence.VerifyPayload`
  verifica forma canonica, due MAC, scope, id e byte-size prima
  di qualsiasi PUT remoto, senza affermare provider/WORM.
  La funzione `CreateSynthetic` esistente rimane SOLO per test;
  un componente di signing reale e custodito in vault è un'altra
  issue. Una retention minore/uguale a 1h da now è negata;
  la policy di durata reale richiede approvazione legale.
- Un PUT usa `IfNoneMatch="*"`, `ObjectLockMode=Compliance`,
  `ObjectLockRetainUntilDate` UTC, payload JSON e
  `MD5Digest` di trasporto S3 (non è autenticazione,
  che usa HMAC-SHA256). Una sola chiamata al client S3.
- La rilettura deve provare anche **modalità S3 COMPLIANCE**
  (non la modalità Azure Locked) e scadenza effettiva
  **non materialmente inferiore alla retainUntil richiesta**
  dal PUT; una differenza inferiore a un secondo è
  ammessa per serializzazione S3 al secondo. Il record
  `ImmutableAttemptObservation` espone modalità e
  scadenza soltanto dopo la verifica HMAC+provider.
  Un provider che restituisce `AzureLocked` o abbrevia
  la retention produce `Uncertain`.
- Il VersionId nell'ACK deve essere non-vuoto,
  non-`null`, non-`latest`. **Non basta l'ACK**:
  `ImmutableAttemptEvidence.VerifyAsync` interroga
  `GetObjectRetention` e `GetObject` di QUEL VersionId,
  verifica COMPLIANCE, scadenza, byte esatti, HMAC e actor.
  Il risultato `Observed` significa **soltanto**
  “versione tentativo vista e validata” (NO source DELETE COMMIT).
- Errori 409/412, timeout, cancellazione, errore SDK/network,
  HTTP 200 senza versione, firma inesatta, rete/readback
  indisponibili o versione diversa restituiscono `Uncertain`
  con `VersionId=null`. **Mai retry automatico o attestazione
  di non-esistenza**. Nessun valore di ritorno autorizza
  DELETE, riuso UUID, release o traffico.
- Un ACK incerto richiede **riconciliazione indipendente
  e operatore autorizzato**, non un nuovo tentativo a
  carico dello stesso processo. Senza un catalogo esterno
  monotono/pin fuori dal database restaurabile, la prova
  CI non risolve la finestra di PITR multi-PGDATA.

## Test e limiti

La suite `S3ObjectLockAttemptSubmissionTests` usa un
container SeaweedFS versioning+Object Lock in CI con
UUID/tenant/actor e HMAC random soltanto sintetici.
Controlla primo PUT COMPLIANCE con GET retained
e tentativo secondo PUT condizionale, DELETE della
versione retained 403, readback pinned dopo conflitto.
Una porta fake inietta perdita ACK dopo una potenziale
scrittura, timeout, 412, VersionId `null` e
retention GOVERNANCE al readback: tutti devono
essere `Uncertain`, senza ulteriori PUT. Test separati
impediscono che payload non canonico, scope foreign
e retention insufficiente raggiungano il PUT.
Questa è una **rehearsal reale del protocollo su
singolo host CI**, NON WORM indipendente da
amministratore/failure domain e NON un test MinIO
in produzione o Azure Blob reale. Nessuna modifica
al PostgreSQL primario o gate di accesso.

## Cosa rimane aperto (P0)

- #144: custody S3 offsite **davvero indipendente** e Azure WORM
  locked in staging, identity/account pin, IAM e
  vault, replica/retention audit, time authority e
  pin monotono **durabile fuori da tutti i PGDATA**.
- #141/#109/#31: signed **source DELETE COMMIT** attestato
  indipendentemente da intent/write-attempt, riconciliazione
  dopo doppio PITR, global ingress/worker/replica gate,
  approvazione operatore e review GDPR su pseudonimi
  e diritto alla cancellazione. Non abilitare release.
- Ricordare che MD5 è una verifica di integrità di trasporto;
  soltanto il payload HMAC lega tenant e identità.
  L'accesso fisico all'host CI può eliminare storage;
  `COMPLIANCE` nel test non dimostra durabilità legale.

## Fonti ufficiali

- [Amazon S3 PutObject, Content-MD5, If-None-Match, VersionId](https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObject.html)
- [AWS SDK .NET v4 PutObjectRequest](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html)
- [Amazon S3 Object Lock, compliance e nuove versioni](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html)
- [AWS SDK v4 retry / MaxErrorRetry](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/retries-timeouts.html)
- [Regolamento UE 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)

## Evidenze RED/GREEN

**RED test-first:** [CI 44891077685](https://github.com/KeyserDSoze/Omnia/actions/runs/44891077685) su `68c5eade` fallisce per i tipi publisher ancora mancanti (CS0246). **GREEN base:** [CI 44891269611](https://github.com/KeyserDSoze/Omnia/actions/runs/44891269611) su `2b25c73c` SUCCESS: 5/5 S3, 4/4 Azure HTTP, 19/19 Domain, 80/80 PG Integration, PITR reali e Docker/mobile; [OIDC 44891269662](https://github.com/KeyserDSoze/Omnia/actions/runs/44891269662) e [Pages 44891269617](https://github.com/KeyserDSoze/Omnia/actions/runs/44891269617) SUCCESS.

**RED regressione policy:** [CI 44891542059](https://github.com/KeyserDSoze/Omnia/actions/runs/44891542059) su `f3826cef`, 5/6 S3 passati e uno fallito per accettazione erronea di mode AzureLocked/retention abbreviata. **GREEN corretta:** [commit 5b237395](https://github.com/KeyserDSoze/Omnia/commit/5b2373953f4c375927077704009315454e7a9ede), [CI 44891839494](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839494) **SUCCESS**: 7/7 S3 Object Lock (condizionale reale SeaweedFS + fault injection), 4/4 Azure SDK mock HTTP, 19/19 Domain, 80/80 PostgreSQL Integration, PITR/Docker/mobile. [OIDC E2E 44891839549](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839549) e [Pages 44891839509](https://github.com/KeyserDSoze/Omnia/actions/runs/44891839509) **SUCCESS**, medesimo SHA. Log: `PHYSICAL_DUAL_CLAIM_PITR_UNSAFE_REPLAY_OBSERVED` e `PHYSICAL_DUAL_CLAIM_PITR_REMAINED_QUARANTINED`; la protezione reale anti-rollback resta aperta.

Le Actions di una eventuale modifica successiva vanno nuovamente
verificate prima di dichiarare `main` verde. **Non chiudere
#144/#141**: storage remoto su host indipendente, authority
monotona, source COMMIT e recovery gate non sono stati sviluppati.
