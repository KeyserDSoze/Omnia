# ADR 0029 — Terza PGDATA per ancoraggio monotono del checkpoint witness (CI-only)

**Data:** 2026-11-20
**Stato:** Accepted per simulazione sintetica (#141, #109, #31); NON è un'ancora immutabile/offsite di produzione.

## Problema

ADR 0028 produce e verifica un prefisso witness autenticato con HMAC,
ma salva il checkpoint in file privato sullo stesso runner di test.
Ripristinare il witness può riportare indietro la sequence locale.
Serve almeno un test di indipendenza della custodia da quella PGDATA,
con storage che rifiuti regressioni senza fidarsi del timestamp sorgente
o di un nuovo checkpoint firmato dopo il disastro.

## Decisione

1. La pipeline crea **una terza istanza PostgreSQL 17 usa-e-getta**:
   `omnia-ci-witness-anchor`, database `omnia_anchor`, loopback 15438,
   `system_identifier` diverso dai cluster primario e witness,
   volume PGDATA separato e password random non condivise.
   La definizione `tests/restore/independent-anchor.sql` è SOLO CI.
2. Account `omnia_anchor_writer` e `omnia_anchor_reader` sono
   nonowner, no SUPERUSER/BYPASSRLS, senza accesso diretto alle tabelle.
   Il writer ha solo EXECUTE della funzione bounded
   `anchor.advance_checkpoint(integer,bytea)`; il reader ha solo
   EXECUTE della funzione `anchor.current_checkpoint()`. Le
   routine hanno `SECURITY DEFINER`, `search_path=pg_catalog,pg_temp`,
   riferimenti alle tabelle qualificati e `REVOKE PUBLIC` prima dei
   GRANT. Ogni metodo .NET esegue guard di ruolo anche quando invocato
   senza l'orchestratore, rifiutando credenziali owner/admin.
3. Il writer prende un lock `FOR UPDATE` sulla head singleton e,
   nella stessa transazione, inserisce il checkpoint in una storia
   append-only e avanza il count. Un count inferiore viene rifiutato;
   un count identico con byte identici è idempotente; un payload
   differente allo stesso count fallisce. Un count superiore può essere
   proposto soltanto dal servizio orchestratore dopo i controlli HMAC.
   La funzione SQL non possiede né verifica autonomamente la chiave HMAC.
4. `PostgresWitnessAnchorIssuer` richiede tre identità separate:
   reader witness, reader anchor e writer anchor. Legge uno snapshot
   witness coerente (ADR 0028), recupera dalla TERZA PGDATA il
   checkpoint PREESISTENTE e lo verifica con `WitnessCheckpoint.Verify`
   prima di creare/salvare un checkpoint nuovo. Se il witness è
   regredito/divergente o non è più disponibile un checkpoint già
   esistente, NON è consentito ri-firmare lo stato ripristinato.
   L'emissione ordinaria `IssueAsync` nega una head assente: solo
   `BootstrapAsync` con `operatorApproved=true` può creare il PRIMO
   checkpoint del test quando l'ancora è vuota; un secondo bootstrap
   viene rifiutato. Il flag booleano NON è un'identità operatore
   autenticata e non è autorizzazione di recovery.
5. `PostgresWitnessAnchorVerifier` è completamente read-only:
   valida il ruolo, recupera l'anchor persistito dal terzo cluster,
   verifica il prefisso rispetto allo snapshot witness e rifiuta
   contenuti manomessi, mancanti o count non corrispondente. Non
   modifica `Recovery__AccessStateFile` né autorizza traffico.
6. TDD: test RED richiede componenti non ancora presenti; ulteriore
   test RED dimostra un reale bypass con credenziali admin passate
   direttamente all'adapter. GREEN verifica snapshot non monotono
   simulato in memoria, conflitti, incremento successivo, permessi e
   tre cluster differenti con PostgreSQL reale. La CI esistente
   continua a testare il PITR FISICO del primario, non del witness.

## Limiti espliciti

TRE database sullo STESSO host CI non costituiscono offsite/geo-DR,
WORM/Object Lock o un'autorità monotona fuori dal controllo di un
amministratore. Un owner della terza PGDATA può restaurarla insieme
al witness; una chiave HMAC sottratta può firmare falsi prefissi; un
writer SQL compromesso può avanzare artificialmente il count
impedendo progressi. Non vengono testati il PITR fisico del witness,
le repliche multiple di anchor, il quorum, vault e firma HSM,
l'accountability del richiedente, source-outbox completeness, la
finestra commit primario→primo ACK witness, la propagazione su media,
cache/provider/device e la riapertura globale con fencing atomico.
I digest minimizzano PII in chiaro, ma non sostituiscono la revisione
GDPR su retention, base giuridica, export/cancellazione delle ricevute.

Restano **OPEN** #141, #109, #31, #32, #33, #125;
nessuna promozione dei servizi o autorizzazione a dati reali.

## Fonti ufficiali

- PostgreSQL 17, locking / FOR UPDATE:
  https://www.postgresql.org/docs/17/explicit-locking.html
- PostgreSQL 17, sicurezza SECURITY DEFINER e search_path:
  https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17, gestione privilegi e REVOKE:
  https://www.postgresql.org/docs/17/sql-revoke.html
- GDPR artt. 5 e 17:
  https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
