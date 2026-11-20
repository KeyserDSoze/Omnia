# Synthetic restore rehearsal (issue #140)

**CI test only. Never execute against user or production data.**

The Omnia CI job starts a disposable pgvector PostgreSQL 17 container and
creates random DB passwords and an ephemeral AES-GCM keyring outside Git.
After the real PostgreSQL xUnit suite, it runs:

```sh
bash tests/restore/restore-rehearsal.sh
```

The script requires the CI container omnia-ci-pg, OMNIA_TEST_APP_DB and
OMNIA_TEST_KEYRING_FILE. The harness executable only connects to
127.0.0.1:15432, user omnia_app, and database omnia_test or
omnia_restore_drill. It will not connect to an arbitrary production host.

1. Seed a synthetic note, consent and 768-dimensional vector via the
   non-owner runtime role; verify the note is already sealed.
2. Perform a real custom-format pg_dump into a protected file within
   the disposable CI DB container; createdb and pg_restore to a second
   database, with restored schema, triggers, RLS and privileges.
3. Keep the old KEK in a keyring with a different active key. Verify
   ciphertext, canonical record ID/AAD, redaction, TTL, vector,
   RLS-protected timeline and JSON export after restore.
4. Re-run with a ring lacking the old KEK and demand cryptographic
   failure rather than returning redacted placeholders or fabricated data.
5. Erase the clone only and re-read the original source record. Remove
   clone and secret files via EXIT trap, including failed runs.

The dump and temporary keys are NOT uploaded as CI artifacts. A dump
can still contain plaintext legacy rows or sensitive metadata; separate
backup encryption, key custody and retention policies remain required.

**Limitations:** same-cluster logical restore, not a physical PITR/WAL
recovery, not a separate cluster's role re-provisioning, not Azure
Flexible Server, not vault recovery, and not erasure of archived copies.
Do not remove historical KEKs or enforce sealed_payload NOT NULL based
on this CI-only result. Parent #138/#33 and #140 remain open.

Decision: [ADR 0015](../adr/0015-synthetic-postgresql-restore-rehearsal.md).

## #109/#140 — Restore in un secondo cluster usa-e-getta (ADR 0016)

Il passo ulteriore rispetto al test ADR 0015 è
`tests/restore/cross-cluster-rehearsal.sh`, lanciato dalla stessa CI
DOPO il restore sintetico al database `omnia_restore_drill`.

Il job avvia `omnia-ci-pg-recovery` su `127.0.0.1:15433` con database
`omnia_recovery`, una nuova directory PGDATA e nuove password random.
Confronta `system_identifier` dei due cluster PostgreSQL per evitare
una falsa separazione. Un restore con i ruoli Omnia mancanti deve fallire;
la CI quindi ricrea il database e provisiona i quattro login limitati
senza copiare i segreti globali del server sorgente. Solo allora esegue
il vero `pg_restore` dell'archivio logico.

L'harness `Omnia.RestoreRehearsal` effettua i check anche nel cluster
secondario come identità `omnia_app`: nessuna visibilità SQL senza
contesto RLS su note/consensi, ID canonico, integrità AES-GCM,
embedding, ricevuto/scadenza, timeline, export e erase. La password
del login originario è rifiutata nel cluster di ripristino. Il test
prova la decifratura mantenendo la vecchia KEK e il fallimento quando
essa non è presente. L'erase nel target non modifica il sorgente.

La cartella privata nel runner e i backup/secret file sono distrutti
anche in caso di errore, insieme al secondo container. Nessun dump
o keyring viene inviato come artifact.

**Attenzione:** entrambi i cluster sono container locali di CI
nello stesso runner; non è un ambiente Azure o geograficamente isolato.
Resta un backup **logico**, non WAL/PITR. Non testa restore del vault,
repliche, object storage, backup/tombstones GDPR o decrittazione di
tutti i record legacy. Non è una procedura utilizzabile per dati reali.

Decisione: [ADR 0016](../adr/0016-independent-postgresql-restore.md).
Documentazione ufficiale:
https://www.postgresql.org/docs/17/app-pg-dumpall.html
https://www.postgresql.org/docs/17/app-pgrestore.html


## Terzo gate CI — basebackup fisico e point-in-time recovery (ADR 0017)

Dopo la prova di restore logico sul secondo cluster (ADR 0016), la CI
invoca `bash tests/restore/physical-pitr-rehearsal.sh` in un ambiente
**totalmente usa-e-getta**: il PostgreSQL 17/pgvector originario archivia
i segmenti WAL solo in una directory temporanea interna al container.

La fase esegue `pg_basebackup -Fp -X stream` e `pg_verifybackup`, poi
registra un evento marker **successivo al backup**, crea un restore point
PostgreSQL nominale, effettua una cancellazione di una **nota sintetica**
nel sorgente e scrive un secondo marker. Una forzatura `pg_switch_wal()`
consente di attendere l'archiviazione del segmento richiesto.

Un nuovo container su `127.0.0.1:15435` parte dal PGDATA della basebackup,
legge il WAL archiviato via `restore_command`, incontra il
`recovery_target_name`, viene promosso e deve contenere il primo marker
ma non il secondo. La nota cancellata sul sorgente deve invece essere
ancora accessibile nel recuperato: la prova verifica il confine temporale
effettivo, non solamente un database che si avvia.

Il processo controlla `system_identifier` invariato per una copia
**fisica**, ruota la password `omnia_app` nel recuperato e rifiuta la
precedente. Esegue l'harness esistente sulla nota ripristinata: RLS,
contenuto AEAD, UUID/AAD, expiry, embedding e export; testa anche la
mancanza della vecchia KEK e la cancellazione della copia.

Le copie temporanee risiedono nel runner e in un volume Docker
usa-e-getta; il cleanup le elimina anche su errore. **Nessun dump
fisico o WAL viene reso pubblico**. La keyring non è nel backup
PostgreSQL e deve essere mantenuta esternamente.

**NON è un restore Azure o una procedura di DR production-ready**:
mancano backup cifrati/persistenti, copie geografiche, policy RPO/RTO,
monitoraggio archivio, Azure Flexible Server, vault recovery, gestione
deleted-data/tombstones, enforcement delle retention e verifica dei
diritti GDPR sulle copie storiche. Fino a questi gate #109/#140 restano OPEN.

ADR: [0017](../adr/0017-physical-postgresql-pitr.md).


## Gate CI successivo al PITR — replay sicuro di cancellazione sintetica (ADR 0018)

Il test PITR ora può usare una ricevuta di cancellazione **fuori dal
backup PostgreSQL**: viene prodotta soltanto dopo il DELETE sintetico
sul sorgente, in una directory privata temporanea del runner. La
ricevuta JSON v1 è autenticata con HMAC-SHA256 e una chiave casuale
non presente nel PGDATA ripristinato. Non compare nei log o artifacts.

Il database fisico recupera inizialmente la nota anteriore al restore
point. Prima del replay, due tentativi con MAC assente o alterato
devono fallire senza effettuare cancellazioni. Il replay autenticato
usa poi `PostgresPersonalDataService.EraseAsync` con actor lock e RLS;
un secondo replay è idempotente, e export/timeline dell'attore cancellato
sono vuoti. Un secondo tenant sintetico rimane invece invariato.

Comandi CI-only dell'harness: `seed-peer`, `sign-receipt`, `reconcile`,
`verify-erased`, `verify-peer`; sono ristretti ai database di test
loopback e agli attori sintetici hard-coded. Il semplice possesso del
file JSON non è una credenziale di autorizzazione per gli utenti reali.

**Limitazioni importanti:** il signer e il verifier girano sullo stesso
runner e non provano l'origine, la durata o la completezza di un journal
esterno: mancano watermark monotono, firme da vault/HSM, documentazione
del diritto di cancellazione, ledger indipendente dai backup, policy di
riapertura del traffico, erasure di cloud object store, connettori e
provider e audit delle copie storiche. Questo è soltanto un test di
replay nell'ambito usa-e-getta: NON riutilizzarlo in produzione.

[ADR 0018](../adr/0018-synthetic-post-pitr-erasure-replay.md).


## Gate applicativo di quarantena HTTP post-PITR (#141, ADR 0019)

L'API ora legge **prima delle migrazioni** un file amministrativo indicato
da `Recovery__AccessStateFile`. In qualunque ambiente non Development
il file è obbligatorio e deve risiedere su un mount riservato, esterno
al PGDATA recuperato e ai backup. Non incorporarlo in immagini Docker,
file Git o artifact della CI. Se il file manca l'API non si avvia.

Il file deve essere regolare, non symlink, con accesso esclusivo del
proprietario (`chmod 400` o `chmod 600`), massimo 32 byte.
Il contenuto è rigorosamente `ready` seguito da LF (newline);
`quarantined` con LF impedisce l'avvio, così come qualsiasi formato
non riconosciuto. Preparare e sostituire il file in modo atomico
attraverso il sistema di configurazione esterno. Il codice non
implementa da solo la custodia del marker o l'autorizzazione dell'operatore.

Se l'API è già attiva, prima di ogni richiesta HTTP viene ricontrollato
il file. Revoca, rimozione o corruzione restituiscono HTTP 503 con
`Cache-Control: no-store` e JSON `recovery_quarantined` su /health
e /api/v1. Il workflow OIDC E2E prova il blocco in un server reale.
In Development, solo l'assenza della configurazione è consentita;
un percorso fornito ma non valido fallisce comunque.

ATTENZIONE: `ready` non attesta alcuna cancellazione, non è firmato
e non dimostra che tutte le ricevute post-PITR siano state rilevate.
Un amministratore in errore può bypassare questa misura impostando ready.
Il gate di questa singola API NON ferma retention worker già attivi,
offline backfill, ingress di altre repliche o richieste in volo.
Non riaprire servizi con dati reali prima del journal anti-rollback
e della quarantena globale di #141. ADR: [0019](../adr/0019-api-recovery-quarantine.md).


## In-process retention worker: quarantena tra batch (#141 / ADR 0020)

Anche il worker `RetentionSweepService`, eseguito nello stesso processo
ASP.NET, ora controlla `Recovery__AccessStateFile` prima di **ogni batch**
di cancellazione TTL. Se manca o indica quarantena, non effettua
ulteriori chiamate a `omnia_purge_expired_memories`; riprende soltanto
durante un ciclo successivo quando il marker torna valido `ready` + LF.
Il ruolo DB è sempre il login non privilegiato `omnia_retention`.

I test PostgreSQL verificano che una nota scaduta rimanga fisicamente
nel DB durante la quarantena e venga eliminata soltanto dopo il
rilascio del marker, senza toccare una nota ancora viva. Un altro
test simula la revoca dopo il primo batch autorizzato e controlla
che il secondo non venga lanciato.

**Non è un fence transazionale**: se il file cambia mentre una query
PostgreSQL è già in corso, essa può arrivare al commit. Non sono
recintati DB access esterni, offline backfill, altre repliche o
l'eventuale lavoro verso provider. La release non è firmata e non
verifica un journal anti-rollback. Per questi gap #141 resta aperta.

Decisione: [ADR 0020](../adr/0020-recovery-gated-retention-worker.md).


## Audit offline di journal + checkpoint dopo PITR (#141 / ADR 0021)

`src/backend/Omnia.RecoveryAudit` è un tool **solo lettura**:
non si connette a PostgreSQL, non elimina dati e non cambia il file
`Recovery__AccessStateFile`. Richiede file **assoluti, privati e
separati dalla copia PGDATA** indicati tramite:

- `OMNIA_RECOVERY_JOURNAL_PATH`: JSON Lines v1 con eventi erase-actor;
- `OMNIA_RECOVERY_CHECKPOINT_PATH`: checkpoint v1 con numero e tip SHA-256;
- `OMNIA_RECOVERY_JOURNAL_KEY_PATH`: chiave HMAC binaria casuale da 32 byte;
- `OMNIA_RECOVERY_CHECKPOINT_KEY_PATH`: seconda chiave HMAC distinta.

Controlla LF framing, firme HMAC-SHA256, catena canonica, sequenze
contigue, UUID unici, schema senza duplicati, dimensioni e coerenza
tra conteggio finale e checkpoint. Un journal troncato viene rifiutato
se il checkpoint esterno non è stato a sua volta riportato indietro.
Rifiuta file mancanti, symlink, group/world read e formati ignoti.
Exit 0 = file verificati contro il checkpoint PRESENTE; exit 2 = errore.
Il tool non stampa tenant, owner, evento o chiavi; solo un totale.

**Non è ancora una procedura reale di emissione e approvazione**:
non esistono producer di cancellazioni su bus/outbox durabile,
storage immutabile o ancoraggio monotono da Azure/OpenBao/HSM.
La chiave e il checkpoint devono essere forniti dall'operatore
indipendentemente dal backup, non creati durante la recovery stessa.
Se vengono troncati o ripristinati insieme al journal, il verificatore
non può attestare la completezza globale. Il risultato non promuove
l'API, non esegue `EraseAsync` e non soddisfa il gate #141.

Vedi [ADR 0021](../adr/0021-recovery-journal-checkpoint-verifier.md).


### Collegamento con il PITR sintetico (ADR 0021)

Lo script `tests/restore/physical-pitr-rehearsal.sh` genera ora
**nel runner usa-e-getta**, dopo la cancellazione della fixture,
un singolo evento erase-actor HMAC, un checkpoint con chiave separata
e due file binari delle chiavi. La CLI offline li verifica prima
del replay HMAC della ricevuta descritto in ADR 0018. Il test svuota
poi il journal e richiede il rifiuto del troncamento senza cambiare
il checkpoint, prima di ripristinare il file e proseguire.

Questo dimostra il wiring in CI del verificatore e del checkpoint
su dati sintetici; la generazione avviene **nello stesso runner**
e non fornisce durabilità, autenticazione di un operatore esterno
o protezione contro rollback simultaneo di journal e checkpoint.
Il tool non autorizza `ready`, non ferma servizi e non cancella dati.


## Emitter locale append-only della fixture PITR (ADR 0022)

La CI genera due chiavi HMAC distinte in una directory temporanea
privata e usa `Omnia.RestoreRehearsal -- append-journal` con il
database **sintetico** (127.0.0.1:15432) per aggiungere l'evento
`erase-actor` al journal e aggiornare il checkpoint.
Il tool di audit indipendente dal database li rilegge dopo l'append.
Questo sostituisce la precedente generazione JSONL in Python.

Il produttore `RecoveryJournalLocalAppender` prende un lock esclusivo
e rilegge/verifica checkpoint e journal prima di aggiungere una riga.
Conferma i dati di file su disco con `Flush(true)` e pubblica un
checkpoint privato da un file temporaneo nella stessa directory.
Se la scrittura viene interrotta tra i due file, il checkpoint
resta indietro e ulteriori append sono rifiutati: la riparazione
manuale NON è automatizzata. Sono testati append multipli, resume
dopo riapertura, mismatch e permessi non privati.

**Non usarlo per cancellazioni reali.** Questo è un produttore locale
di fixture, non una sorgente autorizzata di eventi, un registro
durabile fuori dal failure domain PostgreSQL o un watermark monotono.
Non implementa outbox transazionale, rimozione da backup/provider
o traffic release gate. Il file `ready` non viene aggiornato.
Decisione: [ADR 0022](../adr/0022-local-append-only-recovery-journal.md).


## Outbox di cancellazione nel database primario (#141, ADR 0023)

Le nuove richieste esplicite di cancellazione dell'API inseriscono
un evento `erase-actor` dentro la **stessa transazione PostgreSQL**
dei DELETE di consenso e memoria. Un errore sull'outbox annulla
anche i DELETE; un publisher esterno non è ancora presente.

Il restore logico e il test PITR mantengono RLS e grants sulla
nuova tabella. Ma un PITR antecedente alla richiesta elimina
dal database recuperato anche il relativo evento: la outbox da sola
**non risolve la resurrezione dei dati**. Il journal di test ADR
0022 rimane una fixture distinta, non un worker che pubblichi
eventi autentici dell'API. Blocco API/retention, anti-rollback
esterno e replay restano in #141.

Decisione: [ADR 0023](../adr/0023-transactional-erasure-outbox.md).


## Read-only dispatcher su ruolo separato (ADR 0024)

Nei test PostgreSQL la quinta identità `omnia_dispatcher` viene
creata con password casuale; non ha SELECT diretto sulle tabelle.
Il migrator le concede soltanto EXECUTE di una funzione che
restituisce massimo 100 eventi per richiesta e valida il cursore.
Nel test di restore su cluster indipendente anche la credenziale
del dispatcher viene ricreata, mai importata da `pg_dumpall`.

Una prova di delivery locale legge eventi erasure VERAMENTE
committati dal servizio applicativo e li aggiunge al journal
HMAC ADR 0022 conservandone gli UUID. Una seconda esecuzione
deve lasciare il journal identico; un checkpoint corrotto fa
fallire la prova senza modificare l'outbox.

ATTENZIONE: timestamp e UUID nel cursore NON sono un ordinamento
di commit. Il publisher sintetico riscorre tutta la coda
all'avvio, senza lasciare ACK nel database; un nuovo evento
committato dopo la scansione richiede un passaggio successivo.
Non è un journal esterno affidabile e non prova che tutte le
cancellazioni successive a un PITR possano essere recuperate.

Vedi [ADR 0024](../adr/0024-isolated-outbox-dispatch-and-local-delivery.md).



## Secondo PostgreSQL witness — prova separazione del failure domain (ADR 0025)

Nella CI viene creato omnia-ci-erasure-witness, una seconda istanza
PostgreSQL 17 sulla porta locale 15436. Il suo system_identifier è
verificato differente rispetto al database primario su 15432. Password
e ruoli di writer/reader sono generati esclusivamente dal runner.

Un test crea una cancellazione reale del servizio su dati SINTETICI,
invia il relativo event UUID dell'outbox al witness e poi elimina
soltanto quella riga di outbox dal primario con credenziali admin
di test. La ricevuta rimane interrogabile sul secondo cluster.
Un invio ripetuto non crea una seconda sequence; un UUID con attore
differente viene rifiutato. Le funzioni SQL sono accessibili
solo a login EXECUTE-only che non possono leggere tabelle direttamente.

Questa NON è ancora una prova PITR integrata con lo stesso erase:
la cancellazione della riga da parte del test simula la perdita
dell'outbox sorgente. La pipeline continua a eseguire separatamente
il PITR fisico reale del primario come prova distinta.

Il witness condivide l'host effimero CI, non possiede un archivio
WORM/offsite, high-watermark anti-rollback attestato, vault, publisher
automatico, ACK remoto o replay che autorizzi a togliere la quarantena.
Resta solo un prototipo di sicurezza. Vedi ADR 0025.



## Vero PITR con erasure ricevuta dal secondo cluster (ADR 0026)

Il test ora collega la STESSA cancellazione end-to-end: prende una
pg_basebackup fisica del primario, crea il restore point nominato,
poi invoca EraseAsync (non raw SQL DELETE) che elimina una nota,
un consenso e committa la riga outbox in una transazione.
Il dispatcher consegna l'UUID committato al secondo witness
PostgreSQL indipendente dal PGDATA primario.

Dopo il recupero fisico via WAL al punto precedente, il clone ha
di nuovo la memoria privata, ma non il relativo evento outbox.
La prova rifiuta un writer witness usato come reader senza mutare
il clone; poi richiede una singola ricevuta per l'attore sintetico,
la riconcilia con EraseAsync e ripete per verificare l'idempotenza
dei dati. Export e timeline diventano vuoti; il tenant di controllo
conserva memoria e consenso. Le prove precedenti su HMAC e KEK restano.

La consegna antecedente al PITR è una precondizione artificiale
della CI: non assicura eventi non consegnati prima di un crash,
né completezza del journal, vault, offsite, watermark anti-rollback
o autorizzazione a togliere la quarantena. Solo runner sintetico,
nessun dato reale o endpoint di produzione. ADR 0026:
../adr/0026-witness-reconciliation-after-physical-pitr.md

## Publisher isolato con retry verso il witness (ADR 0027)

La CI compila ora `src/backend/Omnia.ErasurePublisher` e ne invoca
`--once` per verificare sia il rifiuto senza abilitazione esplicita,
sia una scansione effettiva dell'outbox e il commit delle ricevute sul
witness. L'eseguibile non viene avviato nell'API e non configura un
servizio produttivo: accetta solo `CI=true`,
`OMNIA_ERASURE_PUBLISHER_CI_ONLY=1`, 127.0.0.1 e i database/ruoli
sintetici fissi delle due istanze di test.

Senza `--once`, esegue cicli autonomi ogni cinque secondi; errori
di rete o SQL producono un retry con backoff massimo di un minuto.
Un test PostgreSQL simula il witness su porta non raggiungibile,
verifica che la ricevuta manchi, quindi usa il writer corretto,
controlla la conferma persistita sul secondo cluster e ripete
l'invio con una nuova istanza del publisher senza duplicare l'evento.
L'outbox rimane intatta e viene riletta dall'inizio a ogni ciclo.
Il cursor temporale serve SOLO a paginare una scansione; NON è ordine
di commit o watermark affidabile.

Se il sorgente subisce PITR **prima** del primo append riuscito,
l'evento è perduto. Anche dopo una ricevuta non vi è alcuna prova di
completezza globale, né storage offsite, promozione `ready`,
checkpoint anti-rollback indipendente o recovery produzione.
Non considerare il messaggio di ciclo confermato una garanzia di
consegna di ogni cancellazione. #141 rimane aperta.

## Audit di prefisso witness con checkpoint HMAC (ADR 0028)

Una nuova prova CI verifica un prefisso completo delle receipt del
SECONDO cluster PostgreSQL senza accedere alle tabelle come proprietario:
`PostgresErasureWitnessSnapshot.ReadAsync` richiede la identità
`omnia_witness_reader` con sole funzioni EXECUTE, apre una transazione
`REPEATABLE READ, READ ONLY` e scansiona ricevute ordinate con
100 righe per pagina. Rifiuta gap, UUID ripetuti e oltre 10000
righe (sonda della riga in eccesso). Gli append concorrenti non
cambiano lo snapshot in corso.

`WitnessCheckpoint` firma con una chiave HMAC distinta un oggetto JSON
canonico versione 1 contenente SOLO count, tipDigest e mac. Il digest
è il risultato della catena SHA-256 su sequenza e contenuto di ogni
receipt fino a N; tenant e owner non vengono salvati nel checkpoint,
anche se i due sono presenti nel witness e nella memoria transitoria.
La verifica rifiuta chiavi errate, payload alterati, UUID duplicati,
riordino, troncamento e formati JSON ambigui. Un checkpoint di N
eventi NON autentica gli eventi successivi N+1...

Per esercitare l'adapter, la CI usa `Omnia.WitnessAudit --issue` una
sola volta su un file nuovo all'esterno di PGDATA, in directory 0700,
con chiave separata 0600; `--verify` rifiuta checkpoint assente o
manomesso, scrittura ripetuta, uso del ruolo writer al posto di reader
e modalità fuori CI. Nessuna delle due modalità modifica il witness,
promuove un file `ready`, o sblocca la replica restaurata.

**Limite critico:** tutti i file risiedono ancora nel runner temporaneo
CI. L'operatore o un attaccante che ripristini sia witness sia file
checkpoint, oppure possieda la chiave di firma, può occultare la
cancellazione. Un checkpoint emesso DOPO un disastro non prova che lo
stato precedente fosse autentico. Non esiste qui custodia monotona
indipendente, prova della completezza dei DELETE sul primario, o
gestione produzione Azure/on-prem. #141 rimane OPEN.

## Terza PGDATA per checkpoint del witness — ADR 0029

La CI avvia un terzo PostgreSQL 17 (`omnia-ci-witness-anchor`,
127.0.0.1:15438, `omnia_anchor`) con `system_identifier` distinto
sia dal primario sia dal witness. Account `omnia_anchor_writer` e
`omnia_anchor_reader` hanno password indipendenti e soltanto EXECUTE
su due funzioni SECURITY DEFINER, senza accesso diretto alle tabelle.

`anchor.advance_checkpoint(count, payload)` blocca la riga singleton
con `SELECT FOR UPDATE` e scrive append-only il checkpoint in una
transazione assieme al nuovo massimo. Un retry identico restituisce lo
stesso count senza scrivere; un count inferiore o un payload diverso
per lo stesso count viene rifiutato. L'accesso con credenziali owner
agli adapter .NET è negato anche se il chiamante omette l'orchestratore.
Una routine separata, read-only, restituisce l'ultima ricevuta HMAC
(count/bytes) senza esporre tenant o subject.

`PostgresWitnessAnchorIssuer` legge il checkpoint esistente dalla
terza PGDATA e confronta il suo MAC/prefisso con una scansione
REPEATABLE READ del witness prima di firmare o avanzare. Una
riconciliazione negata NON rilascia la quarantena. La prova
PostgreSQL genera un evento sintetico nel witness, ancora il prefisso
nel terzo cluster, ripete l'append senza duplicazione, rifiuta
conflitti/regressioni e verifica la negazione dei privilegi. Simula
poi in memoria il rollback con un prefisso di receipt più corto o
alterato, che deve fallire il confronto con l'ancora sopravvissuta.
Una nuova cancellazione sintetica può incrementare il checkpoint
solo dopo aver autenticato il prefisso precedente.

**Questo NON è un PITR fisico del witness:** il test di rollback è
un confronto con uno snapshot in memoria alterato, mentre il PITR
fisico del primario rimane provato dagli script precedenti. I tre
container condividono host, amministratore e ciclo di vita CI. Un
backup/restore della TERZA PGDATA o la compromissione della chiave
può annullare la garanzia monotona. Nessun WORM/offsite, vault,
proof of completeness prima del primo ACK, release gate globale,
propagazione provider o approvazione GDPR. #141 / #109 OPEN.

### Prima emissione esplicita, mai re-bootstrap implicito

Se `anchor.current_checkpoint()` non restituisce una head,
`IssueAsync` ora FALLISCE: non assume che la storia fosse vuota.
Solo `BootstrapAsync` con approvazione esplicita del TEST sintetico
può inizializzare una head ancora inesistente. Anche `BootstrapAsync`
rifiuta una head già presente. In produzione un segnale booleano
non sostituisce un'identità operatore autenticata, vault/WORM o
attestazione di una storia mai inizializzata; dopo un incidente
l'assenza di head comporta quarantena, non generazione di nuova
baseline. Prova TDD nella issue #141.

## PITR fisico del witness con terza ancora sopravvissuta (ADR 0030)

La pipeline ora verifica anche un vero `pg_basebackup` del cluster
witness (PostgreSQL 17, 15436) con WAL archiviato e restore point
nominato `omnia_ci_before_witness_pitr_erasure`. Un piccolo harness
.NET `tests/Omnia.WitnessPitrRehearsal` limitato a CI crea, con
bootstrap esplicito, una prima ancora HMAC di prefisso in un database
temporaneo `omnia_witness_pitr_anchor` sul TERZO cluster (15438).
Questo database è distinto da `omnia_anchor` usato dagli xUnit e
preserva l'indipendenza fisica dal witness, senza toccare le altre
prove di regressione.

Dopo backup e restore point, una cancellazione sintetica viene
APPEND realmente al witness tramite login EXECUTE-only.
L'orchestratore verifica l'ancora precedente, la avanza e conferma
sul witness live che il nuovo HMAC corrisponda. Il test inserisce un
marker WAL dopo la cancellazione, archivia il segmento richiesto e
ripristina fisicamente un nuovo container dalla basebackup con
`recovery.signal` e `recovery_target_name` sul porto 15439.
`pg_verifybackup` deve accettare il manifest e, nel recupero,
il marker prima-target è presente mentre quello dopo-target NON lo è.

Il clone recuperato ha lo STESSO `system_identifier` del witness
originale (è una copia fisica), ma non la ricevuta di cancellazione
committata dopo il restore point; il terzo anchor cluster ha invece
un identificativo diverso. Il test ruota la password del witness
reader recuperato, nega il login con la password originaria,
richiede che l'audit read-only RIFIUTI la storia regredita e, a
confronto, che ACCETTI ancora il witness non recuperato. Verifica
anche che checkpoint, count e byte HMAC nel terzo database siano
identici prima e dopo la procedura. Nessuna operazione sull'API né
promozione `ready` avviene durante il test.

**Limiti:** è un WAL/PITR fisico REALE del witness, ma primario,
witness, anchor, file WAL e segreti sono sul medesimo runner CI:
NON è disponibilità offsite, Object Lock/WORM, quorum, vault/HSM
o custody anti-rollback indipendente da un amministratore comune.
La cancellazione che andasse persa PRIMA del primo append witness
resterebbe indetectabile tramite questo metodo. Non esiste ancora
un gate globale ingress/API/repliche/workers o la cancellazione
verificata dei sistemi esterni e backup. #141/#109/#31 OPEN.

## Live committed-erasure delivery preflight (ADR 0031)

An offline CI-only component checks the **currently live primary
outbox**, not an already restored database. The dedicated dispatcher
is validated as EXECUTE-only; it opens a single PostgreSQL
`REPEATABLE READ`, `READ ONLY` transaction and scans via
`omnia_read_erasure_outbox` with a hard limit of 1000 events and
an extra-row probe. For each source UUID, the independently
provisioned witness READER checks the acknowledgement and exact
immutable actor/tenant/type scope. Missing witness ACK, changed
scope, unknown source UUID, wrong SQL role or source overflow deny
the audit without writing data. No personal IDs are logged.

The `LiveErasureDeliveryAuditTests` first creates an actual
`PostgresPersonalDataService.EraseAsync` (transactional consent/memory
DELETE and outbox) and expects an unconfirmed result while witness
ACK is absent. A bounded publisher retry then creates the remote
receipt and the read-only audit succeeds, including on a fresh
auditor. A separate in-memory mismatch test checks the immutable
receipt scope without writing a deliberately forged UUID to the
shared witness database. CI invokes `Omnia.ErasureDeliveryAudit
--all` only after the publisher one-shot test, checks wrong roles
and missing opt-in, and emits only a generic success/deny token.

This proves delivery only for the events visible on that **LIVE
primary snapshot**; a primary PITR can remove a committed event
before publication, leaving nothing to compare. It is not proof
of commit-order completeness, external durable ACK, all-device
erasure, or permission to set `Recovery__AccessStateFile=ready`.
#141 remains OPEN.

## Perdita pre-ACK: PITR primario può dare un audit 'pulito' falso (ADR 0032)

Il test CI del PITR FISICO del primario, accanto alla cancellazione
già consegnata al witness, ora crea un SECONDO attore sintetico
completamente separato. Un memory+consent reale viene persistito
PRIMA della basebackup. Dopo il restore point WAL e dopo l'ultima
pubblicazione verso il witness, `PostgresPersonalDataService.EraseAsync`
cancella davvero memory+consent con outbox transazionale. Il nuovo
UUID è presente nella sorgente LIVE, il dato non c'è più, ma il
reader witness EXECUTE-only non trova alcuna ricevuta. La CLI
`Omnia.UnwitnessedPitrRehearsal --live-gap` osserva proprio
questo PENDING ACK.

L'UUID è temporaneamente annotato in un file 0600 nella directory
0700 del runner, fuori dalla PGDATA primaria. Dopo `pg_basebackup`
+ WAL PITR, il clone recupera il memory+consent di quell'attore,
mentre il suo outbox event ID NON esiste più e lo stesso UUID non
è nel witness. La modalità read-only `--recovered-gap` controlla
che `PostgresErasureLiveDeliveryAudit.VerifyAllAsync` possa
riuscire sui soli eventi ancora visibili nel database recuperato,
anche se il precedente UUID committato risulta perso. La presenza
del solo UUID nel file del runner CI è prova di quanto accaduto NEL
TEST, non un ledger affidabile o un'ancora di commit di produzione.

La verifica ruota anche la password di `omnia_dispatcher` nel
clone ed esclude l'accesso con le credenziali pre-PITR, oltre a
negare un writer come reader witness. L'API, con marker locale
`quarantined`, deve ancora negare l'avvio anche dopo il
source-only false-green. Il replay di un ALTRO actor cancellato
e già witnessed deve restare idempotente, ma NON deve far sparire
l'attore perduto nella finestra commit→ACK.

**NON è un recovery gate di produzione:** non si è aggiunta
un'identità firmataria esterna di ogni erase commit. Se il crash
accade prima della registrazione esterna del commit, nessun
verificatore può provare la completezza scansionando solo il
database recuperato. Occorrono audit indipendente immutabile,
fencing globale, vault/HSM e procedure GDPR prima della riapertura
di servizi reali. #141/#109/#31 OPEN.

## Intento remoto PRIMA della cancellazione (ADR 0033)

Il test PITR fisico del primario ora prevede una richiesta di
cancellazione artificiale registrata **PRIMA** del servizio
`EraseAsync`, in una tabella separata
`witness.erasure_intents` sulla SECONDA PGDATA.
La ricevuta registra request UUID, tenant, subject e tipo
`erase-actor-intent`, distinta da `witness.erasure_events`
che conserva gli ACK post-commit. Il valore non è una prova
dell'avvenuta cancellazione e non contiene il suo outbox UUID,
generato solo dopo dal primario.

La CI genera il request UUID e lo salva in file privato 0600
sul runner sintetico. Due account PostgreSQL ulteriori
(`omnia_witness_intent_writer`, `omnia_witness_intent_reader`)
sono separati da scrittore e lettore delle cancellazioni
confermate; hanno solo EXECUTE su funzioni bounded/SECURITY
DEFINER, senza privilegi diretti sulle tabelle. Un record
identico ripetuto è idempotente, uno con lo stesso UUID ma
ambito diverso viene negato. Un writer errato o una CLI senza
opt-in deve essere rifiutata PRIMA di toccare i dati.
Il ledger di intenti non può generare un falso ACK erasure.

Il nuovo `--record-intent` crea l'intento remoto, e
`--verify-intent` verifica che esso sopravviva al PITR.
La successiva cancellazione effettiva viene commit sul
primario ma non pubblicata al witness (finestra già
dimostrata da ADR 0032); il clone PITR resuscita memoria
e consenso ma perde l'outbox. La CLI `--recovered-gap`
osserva l'intento rimasto nel SECONDO cluster, senza
modificare dati né emettere `ready`.

**Limiti:** il flusso è orchestrato SOLO nello script
sintetico CI, non nell'API di produzione; un intento
sopravvissuto non attesta che il DELETE primario sia
committato, che il richiedente abbia autorità, né
autorizza un erase automatico dopo PITR. Gli intenti
non sono ancorati nel terzo cluster e possono a loro
volta retrocedere con il witness; tutte le PGDATA sono
sullo stesso runner temporaneo. #141 e #109 OPEN.
