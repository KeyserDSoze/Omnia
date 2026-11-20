# ADR 0038 — Claim di esecuzione pre-delete a consumo singolo sul terzo cluster

**Data:** 2026-11-20  
**Stato:** Accepted per una regressione sintetica CI; NON protocollo di cancellazione in produzione.  
**Issue:** #141 (P0), #109, #31, #32; dipendenze #33 e #125.

## Problema

L'ADR 0037 impone nel percorso sintetico che
l'intento sia presente in un checkpoint HMAC
sulla TERZA PGDATA prima che il primario
possa eseguire DELETE+outbox. Tuttavia se
il primario viene ripristinato con PITR a
prima di quel commit, può perdere la riga
`erasure_outbox.id` che impediva il
riutilizzo dello stesso request UUID.
Il pre-intent e il suo checkpoint rimangono
validi: una seconda invocazione potrebbe
eliminare dati nuovamente risorti o
acquisiti dopo recovery. Non è consentito
desumere un "success" dal solo intent.

## Decisione implementata

1. Aggiungere un database CI `omnia_execution_claims`
   sulla TERZA PGDATA PostgreSQL 17, separato da
   source, witness e checkpoint. Tabella
   `admission.execution_claims(request_id UUID
   PRIMARY KEY, scope_hmac BYTEA, claimed_at)`
   append-only per i ruoli applicativi.
   `scope_hmac` è HMAC-SHA256 di payload
   canonico con `purpose=omnia-preerase-single-use-claim-v1`,
   UUID, tenant, owner e chiave sintetica
   HMAC già usata per il pre-intent ancorato;
   non memorizza tenant o subject in chiaro.
2. `SECURITY DEFINER admission.claim_once(uuid,bytea)`
   con search_path fidato valida UUID e MAC
   di 32 byte; fa solo `INSERT`. Il duplicato
   genera SQLSTATE 23505 senza
   `ON CONFLICT DO NOTHING`. È deliberatamente
   NON idempotente: un doppio ACK/timeout
   non giustifica il retry. `admission.has_claim(uuid)`
   è sola lettura. REVOKE PUBLIC e
   grant EXECUTE a ruoli separati
   `omnia_claim_writer` e `omnia_claim_reader`
   nella stessa transazione di CREATE.
   Nessun SELECT/INSERT/UPDATE/DELETE sulle
   tabelle raw né privilegi owner/admin.
3. Metodo `EraseWithOneShotAnchoredIntentAsync`
   SOLO in GitHub CI: verifica prima
   il matching intent UUID/tenant/owner
   nel prefisso HMAC già ancorato
   su SECONDA/TERZA PGDATA, poi effettua
   `ClaimOnceAsync` che deve essere
   committata sul TERZO database prima
   che il primario apra qualunque actor
   transaction. Solo dopo esegue
   DELETE memory+consent e INSERT
   stesso UUID in outbox source.
4. Il fallimento o COMMIT ambiguo del
   primario DOPO la claim lascia
   permanentemente un tentativo
   **INCERTO**: non si ritenta, non
   si cancella automaticamente e
   non si proclama il diritto
   all'oblio soddisfatto. Il writer
   claim può anche consumare un UUID
   senza che esista il DELETE;
   si tratta di un rischio DoS
   da gestire con autentica
   autorità, consenso e audit.
5. Test xUnit real PostgreSQL:
   permessi SQL a privilegio minimo,
   presenza/assenza claim, scope HMAC,
   colisione sullo stesso UUID,
   concorrenza 8 chiamate ⇒ esattamente
   un INSERT, tentativo orfano senza
   source COMMIT e rifiuto di un
   secondo DELETE anche in presenza
   di memoria/consenso riacquisiti.
6. `tests/restore/physical-pitr-rehearsal.sh`:
   creare `omnia_pitr_execution_claims`
   sulla TERZA PGDATA rimasta in
   esercizio durante il PITR FISICO
   del PRIMARIO. Dopo ACK pre-intent,
   la prima claim deve persistere
   PRIMA del source DELETE committato
   nella transazione di outbox.
   Dopo vero WAL PITR sul clone
   PostgreSQL 17, verificare che
   source outbox scompaia e
   memoria/consenso ricompaiano.
   La chiamata reale del metodo
   one-shot sul clone promosso DEVE
   essere rifiutata da claim 23505,
   lasciando dati risorti e
   outbox ancora assente, con API
   in quarantena. Il record nella
   TERZA PGDATA non cambia.

## Confini critici

**CLAIM NON EQUIVALE A COMMIT**: il
registro prova solo che un tentativo
è stato consumato, non che il
DELETE source sia stato eseguito
o validato. Non si è implementata
alcuna transazione atomica tra
terzo, witness e primario, né
deduplicazione se anche il TERZO
cluster viene ripristinato/alterato.
Un attore con ruolo claim writer
può causare denial-of-service,
un client può ricevere ACK ambiguo,
ed entrambe le condizioni devono
essere trattate come incerte e
sottoposte a controllo umano.

Tutti i database, ruoli e chiavi
sono nel medesimo runner CI,
non una WORM offsite, vault/HSM,
custodia con dominio amministrativo
separato o prova di COMMIT storico
del primario. Non c'è integrazione
nel REST/OIDC pubblico e nessuna
autorizzazione della richiesta
GDPR o fencing globale di
ingress/API/workers/repliche.
Non sono coperte cancellazioni
in backup, device, provider AI,
cache/media. #141 resta OPEN P0.

## Riferimenti ufficiali

- PostgreSQL 17, Unique Indexes:
  https://www.postgresql.org/docs/17/indexes-unique.html
- PostgreSQL 17, transaction locks and concurrent INSERT:
  https://www.postgresql.org/docs/17/explicit-locking.html
- PostgreSQL 17, SECURITY DEFINER:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17, WAL PITR:
  https://www.postgresql.org/docs/17/continuous-archiving.html
- GDPR Regolamento 2016/679, artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
