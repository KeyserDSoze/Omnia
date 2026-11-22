# ADR 0075 — Revoca scope-consent con identificatore storico e device obsoleti

- Data: 2026-11-22
- Stato: implementazione circoscritta; prove RED/GREEN nell'issue #176
- Issue: #176, parte di #29 (consensi), #41 (multi-device Web), epic #2, test #17
- Riferimenti: ADR 0009 (conservazione) e ADR 0069–0074 (sessione/Web/RLS)

## Contesto

PostgresConsentRepository.GrantAsync serializza già con PostgresActorLock
tutte le operazioni per tenant+subject. Un nuovo grant G2 per
source+purpose imposta revoked_at di G1 e crea una riga nuova, preservando
la cronologia. Un device che non ha ancora sincronizzato la nuova riga
può tuttavia inviare DELETE /api/v1/consents/G1. Prima di questa
decisione RevokeAsync aggiornava solo G1 e rispondeva 204 (successo), ma
G2 rimaneva attivo. Il frontend mostrava "Consenso revocato" mentre
IConsentGuard e la cattura consentivano nuove note. Non era necessario
un doppio grant attivo per riprodurre il problema.

## Decisione

Interpretare la richiesta di revoca di un grant first-party come
**ritiro della finalità e fonte associate a quel grant per l'attore
corrente**, non come aggiornamento del solo ID referenziato. La API
mantiene DELETE /api/v1/consents/{id} e status HTTP esistenti.
L'implementazione usa soltanto tenant+subject **estratti da JWT
validato**, e nella medesima transazione FORCE RLS:

1. Acquisisce PostgresActorLock prima di leggere o scrivere alcuna
   riga. Questo lock è condiviso da grant/regrant, capture, erase e
   revoke per lo stesso attore.
2. Esegue SELECT source,purpose FROM consent_grants WHERE
   id=@id AND tenant_id=@tenant AND owner_sub=@owner FOR UPDATE.
   Non prende source/purpose da query/body/header. ID sconosciuto o
   appartenente ad altro attore -> false/404, senza rivelare
   informazioni su un grant altrui.
3. Esegue UPDATE consent_grants SET revoked_at=@now per tutte e sole le
   righe dello stesso tenant+subject+source+purpose con
   revoked_at IS NULL, nella stessa transazione. Questo include G2 se
   il chiamante possiede ancora solo l'ID storico G1.
4. Non modifica revoked_at delle righe già storicamente revocate; il
   ledger resta esportabile e ispezionabile nel PostgreSQL primario.
   Un ID proprio già revocato resta idempotente (204); una nuova
   autorizzazione **esplicita e successiva** crea un nuovo grant,
   non viene impedita in modo permanente.

Non aggiungere nuove colonne, tabelle, token OAuth, un meccanismo
di "global revocation" del provider né una lista di dispositivi.
Revoca e grant concorrenti sono serializzati per actor dentro il DB,
ma un consenso esplicitamente rilasciato DOPO il commit di revoca può
autorizzare di nuovo. Un device scollegato resta incapace di sapere
immediatamente che un consenso è stato revocato: l'API continua
a impedire catture dopo la revoca. La revoca NON elimina le note già
ricevute, né le copie in backup/provider.

## TDD

- RED: test xUnit contro vero PostgreSQL 17 pgvector con runtime role non
  owner FORCE RLS; G1, G2 superseding, revoca del vecchio G1, G2
  ancora attivo prima del fix. Verifiche negative source diverso,
  subject diverso, tenant diverso, ID non trovato, retention della
  cronologia, negazione di nuove catture e successivo nuovo grant.
- RED: Playwright browser reale Keycloak PKCE/JWT/API/PostgreSQL.
  Al click della revoca G1 un'azione concorrente realmente autenticata
  crea G2 prima che il DELETE G1 venga consegnato. Nessuna risposta,
  token o DB sono simulati. Prima della correzione, refresh recupera
  G2 attivo; dopo la correzione GET consents mostra nessun grant attivo
  e POST memories restituisce 403.
- GREEN: test .NET Domain/PostgreSQL/RLS/S3/Azure, Vitest/TypeScript/
  Expo, static Playwright, real Keycloak E2E, PITR/restore/witness,
  Docker, Pages, **tutte** Actions sullo stesso SHA main.

## Confini privacy/normativi

Il GDPR art. 7(3) richiede che ritirare il consenso sia facile come
concederlo; qui è protetto il solo ritiro del grant first-party
"memory.capture" nel database primario. Non si presume la conformità
dell'intero prodotto. Fuori perimetro rimangono eventuali altre basi
giuridiche, i consensi dei terzi, i dati storici, delete nei sistemi
esterni/provider, il token OAuth e le sessioni IdP, le copie offline,
l'aggiornamento immediato di device scollegati e tutti i backup.
Le epic #29/#2/#41/#17 rimangono OPEN.

## Fonti
- https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng (articolo 7(3))
- https://www.postgresql.org/docs/17/explicit-locking.html
- https://www.postgresql.org/docs/17/ddl-rowsecurity.html
