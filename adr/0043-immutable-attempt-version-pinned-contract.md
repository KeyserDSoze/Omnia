# ADR 0043 — Contratto read-only di evidence immutabile version-pinned (provider-neutral)

**Data:** 2026-11-21  
**Stato:** Accepted **solo come contratto/verifica sintetica**, non come authority produttiva.  
**Issue child:** #143; parent P0: #141 / #109.  
**Precedente:** [ADR 0042](0042-dual-claim-pitr-negative-replay.md).

## Perché

Il PITR simultaneo delle authority TERZA e QUARTA in ADR 0042
fa sparire lo stesso UUID da entrambe e dimostra il replay possibile.
Aggiungere un quinto PostgreSQL nello stesso dominio amministrativo
non correggerebbe questo problema. Occorre costruire e verificare
un confine di trust per una *versione* di oggetto trattenuta in uno
storage indipendente, distinto dalle tabelle ripristinate.

La verifica di una ricevuta precedentemente ammessa non è una
certificazione del COMMIT del DELETE né un'autorizzazione a ripetere
un'operazione. Nessun contratto client può da solo garantire che
il provider non sovrascriva, occulti o cancelli le versioni, né
che il controllo di chiavi/account sia amministrativamente separato.

## Contratto v1

`src/backend/Omnia.Infrastructure/ImmutableAttemptEvidence.cs` definisce:

- `IImmutableAttemptObjectReader.ReadVersionAsync(objectKey,versionId,ct)`:
  l'implementazione **futura** dovrà leggere la versione specificata,
  non il puntatore `latest`, e ottenere attributi immutabilità/retention
  effettivi **per quella stessa versione**; deve fallire su delete
  marker, account errato, permessi mancanti o errore backend.
- `ImmutableAttemptObjectVersion`: identity dello store, chiave
  deterministica `erasure-attempts/v1/{UUID:N}.json`, version ID,
  modalità di immutabilità, scadenza e byte del contenuto.
  Il verificatore confronta identity e version ID con valori
  *attesi forniti da un control plane separato*, non con un dato
  appreso dal PostgreSQL ripristinato. La sorgente reale di questi
  valori **non è ancora implementata**.
- Due chiavi da **32 byte e differenti**: chiave di scope per HMAC
  canonico UUID/tenant/owner, e chiave indipendente di evidence
  per HMAC del payload con purpose
  `omnia-immutable-attempt-evidence-v1`. Il JSON canonico contiene
  solo schemaVersion, purpose, UUID, scope MAC e MAC esterno;
  **mai tenant/owner in chiaro**. Questo non rende anonimi i metadati.
- Rifiuto su schemi/campi inattesi, UTF-8 o JSON non canonici,
  chiavi coincidenti, MAC errati, uuid/tenant/owner non corrispondenti,
  payload oltre 512 byte, store/version non pinned, modalità
  `Unlocked`/`Governance`, retention scaduta, timestamp con
  offset non UTC, oggetto assente o eccezione I/O.
- Per il contratto soltanto, `S3Compliance` oppure `AzureLocked`
  sono **attributi richiesti**; un semplice valore enum restituito
  da un mock **NON è prova di Object Lock reale**. Azure richiede
  policy WORM versione/container effettivamente bloccata. La
  validità temporale dipende dall'orologio passato dal caller:
  la fonte temporale attendibile deve essere verificata nel deploy.
- Un risultato valido `ImmutableAttemptObservation` ha sempre
  `AttemptPreviouslyObserved=true`,
  `MayAutoReplay=false`, `MayReleaseRecovery=false`,
  `ProvesSourceDeleteCommit=false`. Il componente non include
  write API, permessi di nuovo DELETE o rilascio della quarantena.

`CreateSynthetic` crea soltanto byte di fixture per contract test:
non carica oggetti, non procura un version ID reale, non produce
durabilità di commit e non deve essere registrato nel REST.

## Implementazioni future prima di qualsiasi release

1. On-prem: storage S3 compatibile realmente **offsite**, bucket
   con Object Lock enabled e versioning; retention modalità
   COMPLIANCE e lettura `GetObject` + metadati Object Lock
   specifici della versione. Un admin locale condiviso, un
   bucket in GOVERNANCE o un semplice filesystem non bastano.
2. Azure: account Blob con versione/retention immutabile,
   policy bloccata, workload identity e RBAC separati;
   version ID e condizioni provider firmate/controllate
   da un trust domain separato. Test con tenant Azure reale.
3. Entrambi: operatori/chiavi diversi dal database, upload
   write-once verificato **prima** di attestare l'ACK di attempt,
   catalogo monotono/version pin fuori dal ripristino, prove di
   troncamento/list-version/delete-marker, interruzione di rete,
   aggiornamenti concorrenti e tempi/retention, alert RPO/RTO,
   firma e verifica di commit primario (protocollo ancora da
   definire) e revisione GDPR incl. data minimization.
4. Fencing di API, in-flight, worker, cache, repliche e provider
   con rilascio operatore autenticato solo dopo riconciliazione.
   Queste attività restano nella **#141 OPEN**.

## Fonti ufficiali

- [AWS S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html):
  COMPLIANCE e GOVERNANCE hanno garanzie e privilegi distinti;
  versioning è necessario.
- [AWS GetObjectRetention / gestione](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html):
  retention si verifica per *object version*, non per un nome logico.
- [MinIO Object Lock](https://min.io/docs/minio/kubernetes/upstream/administration/object-management/object-retention.html):
  prerequisiti Object Lock, versioning e modalità compliance.
- [Microsoft Azure Blob immutable storage](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview):
  WORM con policy a livello versione o container e policy bloccata.
- [Azure version-level policy](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-policy-configure-version-scope).
- [GDPR UE 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/):
  basi giuridiche e retention minima/necessaria da definire.

## Test / stato

`tests/Omnia.Integration.Tests/ImmutableAttemptEvidenceContractTests.cs`
contiene casi positivi e negativi per scope, due chiavi,
pinning version, identity, retention, modalità, serializzazione
canonica, assenza oggetto e eccezioni I/O. Tutti i provider
sono **mock contrattuali**, non Azure o S3 reali. Le evidenze
dei workflow RED/GREEN vengono registrate nelle issue dopo
completamento; non attribuire conformità a un test con stub.
