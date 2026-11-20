# ADR 0037 — Cancellazione sintetica solo dopo ACK HMAC del pre-intent sulla terza PGDATA

**Data:** 2026-11-20  
**Stato:** Accepted, regression di sicurezza SOLO in GitHub Actions CI; NON integrata nell'API di produzione.  
**Issue:** #141 P0, #109, #31, #32; dipendenze #33 e #125.

## Problema

Le ADR 0033–0036 creano pre-intent e successivo
checkpoint HMAC monotono su un database della TERZA
PGDATA. Tuttavia il percorso di cancellazione
sintetico della ADR 0034 richiedeva soltanto
che l'intento fosse presente sulla SECONDA PGDATA.
Se il DELETE fosse stato confermato nel primario
prima dell'ACK del checkpoint terzo, un PITR
del witness avrebbe potuto perdere l'intento
e quello del primario perdere sia DELETE
sia outbox. Anche il test fisico verificava
il checkpoint in modo separato dal DELETE.

## Decisione implementata e testata

1. Aggiungere un nuovo metodo esplicito
   `PostgresPersonalDataService.EraseWithAnchoredIntentAsync`
   sotto guardia `CI=true`/`GITHUB_ACTIONS=true`
   (non si tratta di autenticazione reale).
   **Non collegarlo** al REST endpoint o alla
   interfaccia applicativa pubblica; conservare
   `EraseAsync` attuale. Il vecchio metodo
   sperimentale `EraseWithWitnessedIntentAsync`
   resta per i test storici, ma non impone
   l'ACK terzo: non considerarlo il nuovo gate.
2. Il nuovo `PostgresPreEraseIntentAnchorVerifier
   .VerifyRequestAsync` verifica ruoli reader
   EXECUTE-only di seconda e terza PGDATA,
   presenza del checkpoint, key HMAC 32 byte,
   formato canonico e prefisso SHA256 continuo
   in UN SOLO snapshot witness `REPEATABLE READ
   READ ONLY`. Richiede che il request UUID
   sia nello stesso prefisso con
   `sequence <= anchoredCount` e corrisponda
   esattamente a tenant, owner e tipo. Un
   record appena aggiunto ma non ancora
   ancorato DEVE causare un errore.
3. Solo quando il checkpoint verificato include
   l'intento dell'attore, aprire la transazione
   del primario, cancellare memoria+consenso
   sotto RLS e actor-lock, e scrivere
   `erasure_outbox.id = requestUUID` nella
   stessa transazione. Gli errori di chiave
   errata, scope, SQL reader, anchor mancante
   o non aggiornato devono precedere ogni
   mutazione. Un retry con outbox originale
   presente fallisce con 23505 e rollback
   di acquisizioni successive. Dopo PITR
   del primario questa protezione locale
   potrebbe essere persa: non ritentare
   automaticamente l'intento.
4. Il test xUnit su PostgreSQL reale isola
   `omnia_preflight_anchor` in un database
   dedicato del terzo PGDATA, per evitare
   che suite concorrenti con chiavi diverse
   condividano il checkpoint. Verifica
   negazione prima di inserimento intent,
   dopo append ma prima di bootstrap,
   errori di HMAC, scope e SQL role; conferma
   che un nuovo intent oltre la testa
   ancorata sia ancora rifiutato. Verifica
   poi il successo di un intento ancorato,
   l'UUID nel commit outbox, l'avanzamento
   dopo nuovi intenti e l'assenza di
   cancellazione in caso di duplicate UUID.
5. Il test di PITR FISICO primario crea un
   ulteriore database `omnia_pitr_preflight_anchor`
   e una chiave random 32 byte solo in
   file temporaneo runner (0600). Prima del
   DELETE verifica che un pre-intent appena
   registrato ma non ancorato sia NEGATO:
   memory ancora presente e outbox vuota.
   Invia quindi il checkpoint HMAC alla
   TERZA PGDATA tramite bootstrap esplicito
   (rifiutando bootstrap ripetuto).
   Solo dopo il suo ACK ripete
   `EraseWithAnchoredIntentAsync`, che
   registra un commit primario senza
   post-commit witness ACK. Il PITR fisico
   ripristina memoria+consenso e perde
   il DELETE e l'outbox; sul secondo/terzo
   PGDATA l'intento ancorato rimane integro,
   il checkpoint non deve cambiare e la
   classificazione resta `CommitUnknown`
   con API in quarantena. Nessun `ready`
   o replay automatico.

## Confini espliciti

Questo test dimostra che **questo specifico
metodo sintetico** attende la persistenza
dell'intento nel terzo cluster prima di
procedere al DELETE. Non prova che tutti
i percorsi API/worker siano obbligati a
farlo. Non esiste un transaction manager
che renda atomiche le transazioni witness,
anchor e primary: il checkpoint firmato
può essere conservato senza successivo
DELETE, il source COMMIT può restare
sconosciuto dopo crash/PITR e un leader
privilegiato può manipolare l'intero
ambiente CI. I flag CI non sostituiscono
OIDC, consenso e autorizzazione utente.

Tutte le PGDATA, password e chiavi sono
su un solo host temporaneo. Non è stata
implementata la custodia esterna WORM,
la gestione HSM/vault, il rollback
dell'anchor, il fencing universale
di ingress/API/worker/repliche, una
fonte firmata indipendente di COMMIT
primary né cancellazione di provider,
backup, media, cache e device. **La #141
resta OPEN P0**; conformità GDPR
non affermata.

## Fonti tecniche

- PostgreSQL 17, isolation and read-only snapshot:
  https://www.postgresql.org/docs/17/sql-set-transaction.html
- PostgreSQL 17, two-phase transactions / no implicit multi-database atomicity:
  https://www.postgresql.org/docs/17/two-phase.html
- PostgreSQL 17, transaction commit and rollback:
  https://www.postgresql.org/docs/17/tutorial-transactions.html
- PostgreSQL 17, WAL PITR:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- GDPR Regolamento UE 2016/679, artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
