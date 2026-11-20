# ADR 0026 — Riconciliazione post-PITR con ricevuta da witness separato

**Data:** 2026-11-20
**Stato:** Accepted solo come test sintetico CI (#141/#109/#31); NON come release gate di produzione.

## Problema

ADR 0017-0018 provavano il PITR fisico di PostgreSQL e un replay da ricevuta HMAC temporanea, ma la mutazione sul sorgente era un raw SQL DELETE, non una richiesta completa di cancellazione con outbox. ADR 0025 provava una ricevuta su un secondo cluster dopo la cancellazione manuale di una riga outbox, senza eseguire il PITR di QUELLA stessa cancellazione. Mancava quindi la prova end-to-end su uno stesso evento realmente committato da EraseAsync.

## Decisione e flusso testato

- Un pg_basebackup fisico con WAL stream viene acquisito sul PostgreSQL 17 primario. Dopo il backup viene creato il restore point WAL nominato omnia_ci_before_destructive_change.
- DOPO il restore point, la fase source-only erase-source invoca la vera PostgresPersonalDataService.EraseAsync con identità fissa di test. Memoria, consenso ed evento erase-actor nell'erasure_outbox sorgente sono confermati nello stesso commit, sotto actor lock e RLS. Il test asserisce una nota e un consenso eliminati e un evento outbox.
- Prima del restore, la fase publish-source usa le credenziali EXECUTE-only del dispatcher primario, del writer witness e del reader witness, tutte vincolate a 127.0.0.1 e ai nomi di database e ruolo di test. Invia l'UUID dell'evento davvero committato al SECONDO cluster PostgreSQL dell'ADR 0025 e verifica una singola ricevuta dell'attore sintetico tramite scansione bounded (max 10000).
- Con WAL archiviato e pg_verifybackup, il clone fisico viene promosso al restore point PRIMA dell'erasure. Il test verifica di nuovo il dato personale resurrected e l'assenza della relativa riga outbox, ora realmente perduta per PITR. L'altro attore sintetico rimane nell'immagine.
- Prima di riconciliare, l'harness prova a usare una connessione witness writer come fosse reader: deve fallire senza modifiche al clone. I precedenti controlli negativi di receipt HMAC mancante/manomesso continuano a essere richiesti.
- Solo la fase witness-reconcile, consentita esclusivamente su database di test 127.0.0.1:15435, valida il login witness reader sul secondo cluster 127.0.0.1:15436 e scansiona la sequence ordinata. Richiede esattamente una ricevuta erase-actor con tenant+subject noti, sequence positiva e UUID valido; solo allora invoca EraseAsync con actor lock/RLS sul clone. Nessun input permette di selezionare un altro utente o database.
- La riconciliazione viene ripetuta per provare idempotenza del risultato, poi si controllano timeline ed export vuoti per l'attore eliminato, consenso e nota di un altro tenant intatti, e il tradizionale receipt HMAC come percorso di replay addizionale.
- Non vengono avviati endpoint pubblici dell'API né impostato un marker ready. Runner, container, chiavi e dati sono sintetici e usa-e-getta; il log non espone identificatori reali o segreti.

## Confini irrisolti

La consegna al witness è effettuata nel test PRIMA del PITR. Se il primario perde i dati DOPO il commit ma PRIMA della consegna, anche l'outbox scompare e il witness non può provarne l'esistenza. Il test non dimostra che tutti gli eventi siano stati consegnati o che le richieste fossero giuridicamente valide.

Il witness è un PostgreSQL separato ma gira SULLO STESSO runner CI e resta modificabile dal suo amministratore; la sequence locale non è un high-watermark contro il rollback di entrambi i database. Mancano registro WORM/offsite, attestazione con chiavi HSM/Key Vault/OpenBao, publisher schedulato con retry e ACK durevoli, quorum o blocco globale ingress/repliche/worker, erasure di provider, cache, backup e device, policy di retention e GDPR. L'harness non è un componente produttivo di recovery e non autorizza a sbloccare Recovery__AccessStateFile. Le issue #141/#109/#31/#32/#125/#33 rimangono OPEN.

## Fonti ufficiali

- PostgreSQL 17 archiviazione e PITR: https://www.postgresql.org/docs/17/continuous-archiving.html
- PostgreSQL 17 restore point: https://www.postgresql.org/docs/17/functions-admin.html
- PostgreSQL 17 function security: https://www.postgresql.org/docs/17/perm-functions.html
- GDPR art. 17: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
