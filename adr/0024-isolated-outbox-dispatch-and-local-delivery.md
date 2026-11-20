# ADR 0024 — Dispatch read-only isolato e consegna idempotente al journal sintetico

**Data:** 2026-11-20  
**Stato:** Accepted per slice CI di #141; non è publisher affidabile di produzione.

## Contesto

L'ADR 0023 introduce `erasure_outbox`: `EraseAsync` emette un evento
nello stesso commit della cancellazione di note/consensi. L'API ha
soltanto INSERT con FORCE RLS. Per rendere gli eventi esportabili
verso un sistema indipendente servono un lettore con un'identità
separata, una modalità di paginazione limitata e una consegna che
non duplichi eventi quando un worker riparte.

## Scelta implementata

- Migrazione `0008_erasure_outbox_dispatch.sql`: una policy SELECT
  è attribuita esclusivamente all'owner dello schema su una tabella
  che mantiene FORCE RLS. La funzione PostgreSQL
  `omnia_read_erasure_outbox(integer,timestamptz,uuid)` è
  `SECURITY DEFINER` con `search_path = pg_catalog,pg_temp`,
  SQL statico, massimo **100 righe per chiamata** e keyset temporale
  `(created_at,id)`; rifiuta input non validi e cursori parziali.
  `EXECUTE FROM PUBLIC` è revocato nella stessa transazione
  di creazione. Non esistono query arbitrarie o filtri tenant
  controllabili dal chiamante.
- Solo se l'operatore configura esplicitamente
  `PostgresMigrator(..., dispatchRole:...)`, viene concesso
  `EXECUTE` della funzione a un login distinto, non SUPERUSER,
  non BYPASSRLS, non owner e senza privilegi diretti su outbox,
  memoria o consensi. Non viene creata una password implicita.
  Le identità `omnia_app`, `omnia_retention`,
  `omnia_backfill` e `omnia_backfill_audit` NON possono invocare
  questa funzione né leggere l'outbox.
- `PostgresErasureDispatchReader` ricontrolla l'identità con
  `VerifyRoleAsync`, restituisce solo UUID evento, tipo, ID tenant,
  subject e timestamp, senza contenuto, vettori o note.
  Espone `ReadBatchAsync` con `batchSize` limitato a 1–100 e
  verifica che entrambi i campi del cursore siano presenti insieme.
- `RecoveryJournalLocalAppender.AppendKnownEvent` aggiunge eventi
  al journal HMAC locale conservando l'UUID che arriva da
  PostgreSQL. Prima di ogni modifica verifica l'intera catena
  e il checkpoint. Un UUID già presente con lo stesso attore
  e operazione è un no-op idempotente; un riutilizzo conflittuale
  viene rifiutato. Il vecchio `Append` per la fixture sintetica
  continua a generare il proprio UUID.
- `PostgresErasureOutboxLocalPublisher` è un **bridge di prova**
  non registrato come servizio API né esposto via HTTP: verifica
  l'identità del reader, legge le due chiavi HMAC private e
  scorre fino a 1000 eventi per esecuzione, scrivendoli nel
  journal locale. Ogni nuova invocazione ricomincia dall'inizio
  e usa l'UUID dell'evento per non duplicare consegne.
  Superare il limite interrompe il processo con errore, senza
  dichiarare che la coda è stata svuotata. Nessun ACK viene scritto
  in PostgreSQL; un fallimento del file lascia l'evento primario
  disponibile per un nuovo tentativo.
- GitHub Actions crea un quinto login casuale `omnia_dispatcher`,
  lo usa esclusivamente nei test PostgreSQL, e verifica che il
  recupero su cluster indipendente non importi le password
  originarie ma ricrei anche questa identità.
- Test xUnit su database vero verificano ruolo distinto, accesso
  vietato ad altre tabelle/funzioni, paginazione limitata,
  eventi confermati dalla reale `EraseAsync`, UUID stabile
  nell'append, replay senza duplicati, checkpoint invalido
  bloccante e richiesta tentata con login API rifiutata.

## Rischi e limiti

**La funzione esegue una lettura globale dei metadata sotto un
login privilegiato funzionalmente.** Comprometterne le credenziali
espone potenzialmente gli identificativi di tenant e utenti:
limiti di query non equivalgono a rate limiting, controllo di
scopo o segregazione per tenant. Nessun publisher è abilitato
per default in produzione. Gli ID restano dati personali.

`created_at` è assegnato prima del COMMIT: l'ordine
`(created_at,id)` NON coincide necessariamente con l'ordine di
commit PostgreSQL. Non deve mai essere memorizzato o usato come
**watermark anti-rollback o ACK definitivo**: una transazione
concorrente può confermare più tardi con timestamp antecedente.
Il bridge sintetico ricomincia deliberatamente dal primo evento
a ogni esecuzione; in produzione servono un protocollo di delivery
completo, deduplica esterna durabile e un'ancora monotona reale.

Il journal di destinazione è sul filesystem del runner test,
non in object storage indipendente, e il suo checkpoint locale
può essere ripristinato insieme al journal. La consegna non
fornisce semantica exactly-once, prova di persistenza remota,
firma di una richiesta GDPR, Key Vault/OpenBao, outbox ACK
certificato, locking distribuito, automatic worker scheduling
o gate di rilascio API/repliche. Un PITR anteriore al DELETE
può ancora perdere la riga dell'outbox. Le prove non usano
dati reali né attestano completamento dei diritti GDPR.

**#141, #31, #109, #125 e #33 rimangono OPEN.**

## Riferimenti

- PostgreSQL 17 FORCE RLS: https://www.postgresql.org/docs/17/ddl-rowsecurity.html
- PostgreSQL 17 SECURITY DEFINER: https://www.postgresql.org/docs/17/sql-createfunction.html
- PostgreSQL 17 GRANT: https://www.postgresql.org/docs/17/sql-grant.html
- GDPR: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/