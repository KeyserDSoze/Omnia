# Omnia — threat model / privacy gates (draft)

## Data classes

- Sensitive: third-party conversations, recordings, calendar/CRM content, behavioral inferences, location, biometric/voice characteristics. Data may belong to other people, not only the account owner.
- Core threats: cross-tenant leaks, prompt injection from external messages/MCP responses, fraudulent autonomous communication, lateral movement from an untrusted connector, OAuth token theft, unwanted recordings and model-provider data exfiltration.
- Trust boundaries: device → OIDC token → API → external connector/AI provider → PostgreSQL/storage. GitHub Pages assets and `EXPO_PUBLIC_*` values **must never** contain confidential material.

## Non-negotiable product requirements

1. Consent record before each data class/integration; visible indicator during any recording; record applicable party consent, context and local law. Do not silently record phone calls.
2. Explicit scope for *who*, *what*, *where*, *how long*, *which provider*; provide pause, revoke, retention limit, delete and export.
3. Never use unrelated third-party conversations to impersonate a contact. Voice cloning requires separate explicit authorization and anti-impersonation controls.
4. External messages, MCP tool metadata and retrieved content are untrusted input, **never instructions**. Isolate executable tools, restrict network/file scopes, quota, timeout and secrets per connector.
5. No autonomous send, purchase, publication or destructive action without an appropriate user-authorized policy; provide audit and revocation.
6. Dedupe by trusted user+source+external ID, tenant ID derived only from signed JWT. Test authorization at API and repository boundary.
7. Use encryption at rest and in transit, secret vault, token rotation, deletion propagation and retention expiries **before production**.
8. AI feedback is non-diagnostic, explainable, easy to correct and opt-in. Avoid claims of objective assessment of emotions/personality.

## Platform reality

- Native recording of the **app's microphone** is feasible with permission. Recording two sides of ordinary iOS/Android cellular calls is not generally available to third-party cross-platform apps through a universal public API; OS, device, market and consent rules differ. Import existing recordings or integrate authorized conference providers as separate flows.
- WhatsApp personal inbox access cannot be assumed equivalent to approved WhatsApp Business Platform integration.
- GitHub Pages only hosts the static frontend. Azure API requires HTTPS, OIDC, regulated PaaS setup and CORS allowlisting.

## Current foundation limitations

The MVP stores raw text unencrypted by the application (transport and managed-disk protections must be configured separately), has no retention/delete or full audit, no API throttling, no RLS, no consent ledger, no PKCE client integration, no deployed Azure topology and no automated real OIDC browser E2E. **Use synthetic data only.**

## Scoped consent gate (first vertical slice of #29, 2026-11-17)

La tabella SQL `consent_grants` e i servizi DI permettono `memory.capture` solo dopo una richiesta autenticata contenente esplicita accettazione, policy version e scadenza limitata a 90 giorni; ogni grant è delimitato dal `tenant_id` e `sub` verificati. Una nuova concessione revoca l'eventuale grant precedente nello stesso scope (mantenendo la storia). La revoca è idempotente e filtrata per tenant/subject. La pipeline controlla il grant **prima** delle chiamate AI e nuovamente sotto `FOR SHARE` nella transazione SQL di scrittura. Le nuove acquisizioni senza grant o con grant scaduto/revocato falliscono con HTTP 403.

**Rischi ancora aperti:** il booleano di accettazione API deve essere collegato a un'interfaccia OIDC/PKCE autenticata con testo informativo reale; non prova presenza fisica né consenso dei terzi; non esiste ancora un ledger dei consensi per destinazioni terze/OAuth, enforcement in job di sync/agent, cancellazione dei raw/derived, RLS, per-categoria consent, audit append-only o garanzie su AI in-flight prima della revoca. La sezione baseline sopra che descrive "no consent ledger" si riferisce allo stato precedente alla slice #29. Il progetto resta inadatto a contenuti reali fino agli altri gate privacy.

## API application rate limiting (slice of #37)

Rate limiter fixed-window per signed JWT tenant+sub (default 120 requests/60s; no queue) su `/api/v1/memories` e `/api/v1/consents`, eseguito dopo autenticazione. Anonymous/invalid claim requests share a fallback peer-IP partition; forged `X-Forwarded-For` header is not used. HTTP 429 with JSON `rate_limited`, `Retry-After: 60`. `/health` remains probe-friendly. Native test HTTP uses a fixture auth handler **not enabled in production**, while real OIDC/JWKS browser end-to-end security remains #17. Missing: distributed store/shared quota across Container Apps replicas, connector limits, upload/media quotas, reverse proxy/WAF/DDoS, audit and load tests. Feature #37 remains open.

## Web OIDC and scoped capture UI (first slice #39/#41)

Adottato `oidc-client-ts` per state/nonce validation e OAuth PKCE S256 (non ad hoc crypto). Refresh/access tokens sono in **memory-only** userStore: un reload richiede nuova autenticazione; il PKCE request state per il redirect è in sessionStorage con TTL gestito dalla libreria. Browser callback ripulisce il codice dalla URL; cliente non conserva password o client_secret. Le fetch API non seguono redirect, non usano cookie, e usano un'origine con HTTPS salvo loopback. Il grant manual user-first viene scelto dopo testo informativo con accepted=true; niente consenso automatico. Backend continua a validare JWT e consenso prima di invocare AI.

Limiti: non ancora certificata la user experience del rinnovo token/SSO e logout globale IdP; il logout locale tenta revoca token ma non chiude necessariamente la sessione SSO del provider. La Pages pubblica non dispone di backend Azure e resta solo demo. Native iOS/Android usa ancora demo e non un login fittizio. La UI non rende legale registrare conversazioni di terzi e non sostituisce DPIA, delete/export e RLS.

## PostgreSQL Row-Level Security (feature #30, 2026-11-17)

- `0003_rls_scope.sql` abilita `ENABLE ROW LEVEL SECURITY` **e** `FORCE ROW LEVEL SECURITY` su `memory_entries` e `consent_grants` con policy su ogni operazione `USING`/`WITH CHECK`. Senza `omnia.tenant_id` e `omnia.owner_sub` (o con stringhe vuote) non è visibile nessuna riga e INSERT fuori scope è rifiutato.
- Ruolo runtime `omnia_app`: non è table owner, superuser, BYPASSRLS o membro del ruolo owner; possiede unicamente i grant DML richiesti. Ruolo distinto di migrazione owner/admin. `PostgresRuntimeGuard` blocca l'avvio se l'account runtime può bypassare la policy o se non è forzata.
- Nessuna query personal-data senza `PostgresActorScope.OpenAsync`: il contesto tenant+subject deriva esclusivamente dal JWT verificato dall'API, viene impostato con `set_config(..., true)` **all'interno** della transazione e sparisce a COMMIT/ROLLBACK. I repository continuano ad applicare WHERE espliciti: difesa a strati.
- Tests: `RlsIsolationTests` usa PostgreSQL reale, connessione senza WHERE, ruolo diverso per query amministrative, due owner nello stesso tenant, un altro tenant, tentativi INSERT/UPDATE cross-owner, `row_security=off` e riutilizzo del pool con MaxPoolSize=1.
- **Limitazioni:** la policy basata su GUC custom non è una barriera a SQL arbitrario controllato da un attaccante (tali parametri possono essere modificati dalla stessa sessione SQL); rimangono necessari comandi parametrizzati e controllo delle query/injection. Superuser e BYPASSRLS *bypassano anche FORCE RLS*, quindi l'API non può usarli. Il ruolo schema-owner è riservato alla procedura di migrazione; in produzione isolarlo operativamente fuori dal processo API, gestire le credenziali con Key Vault ed evitare logging di connection string. Una migrazione malevola/compromessa rimane privilegiata.

## Export e cancellazione del database primario (slice #31)

Le nuove API privacy sono JWT/OIDC-authenticated, rate-limited e delimitate da RLS su `tenant_id` e `sub`, senza possibilità per il client di indicare l'owner. Export con `Content-Disposition: attachment`, `Cache-Control: no-store`, JSON streaming e nessun log dei contenuti. L'export include raw, normalized, summary, tags, embeddings (se presenti), data di ricezione e concessioni di consenso, anche revocate. La risposta contiene dati sensibili: il browser ne avvia il download solo su azione dell'utente, ma l'utente resta responsabile del file esportato.

`POST /api/v1/privacy/erase` richiede un body `confirmation: "ERASE_MY_DATA"` oltre alla sessione OIDC, e la UI richiede un consenso informato checkbox e frase esatta. La cancellazione elimina le due attuali tabelle personali in una transazione e un lock condiviso per attore con ingest/grants/revoke/export; il ritorno attesta **solo l'eliminazione nel database primario**. Nessun id/owner personale è inserito in una receipt persistente in questa fase. **Non** dichiarare eseguita la cancellazione da backup/WAL, device audio locali, identità Keycloak, log, archivi di fornitori di AI o futuri object store/connettori. Non è implementato un servizio di gestione delle eccezioni di legge, verifica identità rafforzata per azioni distruttive o audit di erasure persistente.

Legal analysis: GDPR art. 15 (accesso/copia), 17 (cancellazione ed eccezioni), 20 (portabilità, con campo di applicazione distinto), 19 (comunicazione ai destinatari). Fonti ufficiali: https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/ ; EDPB 2025 right-to-erasure coordinated enforcement report: https://www.edpb.europa.eu/system/files/documents/2026-02/edpb_cef-report_2025_right-to-erasure_en.pdf . L'export tecnico non costituisce da solo una risposta formalmente completa a tutte le richieste ex GDPR: il titolare deve verificare diritti di terzi, basi legali, obblighi di conservazione e procedure documentate.

## Retention e ruolo minimo di pulizia (#136, 2026-11-18)

La migrazione `0004_memory_retention.sql` registra `expires_at` per ciascuna nota (30 giorni da `received_at`, tempo del DB), includendo le righe storiche in modo da non estenderne la conservazione al momento della migrazione. `RecentAsync` nega la lettura via API delle righe scadute prima del purge. La cancellazione fisica è affidata a una stored function `public.omnia_purge_expired_memories(integer)` con testo SQL statico, `SECURITY DEFINER`, ricerca funzioni solo su schema trusted, limite 1..500 e `FOR UPDATE SKIP LOCKED`. È revocato `EXECUTE` da PUBLIC, `omnia_app` non lo possiede; il solo ruolo `omnia_retention` ha `EXECUTE` e **nessun SELECT/DELETE sui dati**. Il worker ha credenziale autonoma distinta da API e migratore, verificata a startup con fail-closed. L'API background service elimina fino a 5.000 note/ciclo, prima all'avvio e poi ogni 15 minuti; logga soltanto conteggi e tipi di errori, mai testo o identificativi di utente. Test SQL reali verificano permessi, scadenza, batch e altri tenant.

**Rischio residuo:** la funzione SECURITY DEFINER opera con privilegio di schema-owner, quindi la sua definizione, migration pipeline e i ruoli che possono alterarla sono codice ad alta fiducia. Le tre credenziali restano nello stesso processo API: separare il worker in workload/identità isolata su Azure. La pulizia da backup/WAL, le copie AI, i consensi, l'audit, il logging e le regole per finalità sono ancora non implementati. La scelta provvisoria di 30 giorni richiede una giustificazione documentata specifica (GDPR art. 5(1)(e)) e non implica che ogni tipo di dato debba avere lo stesso periodo.

## Envelope AES-256-GCM (prima base #137 per #33, non integrata nel DB)

`AesGcmEnvelopeEncryption` usa chiavi dati casuali (DEK) 256-bit per ogni payload, una KEK 256-bit per ID/versione, due nonce casuali distinti di 96 bit e tag di 128 bit per payload e wrapping. Il contesto AEAD usa codifica UTF-8 con campi delimitati da lunghezza per tenant, owner, finalità e record ID: un ciphertext non deve aprirsi con un altro soggetto, record o scopo. Il wrapping autentica anche il key ID, la versione del formato è esplicita e i dati manomessi vengono rifiutati. I buffer KEK/DEK temporanei vengono azzerati con `CryptographicOperations.ZeroMemory`; non loggare materiali crittografici, plaintext né ciphertext. Le chiavi vecchie devono rimanere disponibili sino alla conclusione e verifica di un rewrap di TUTTI gli oggetti; la sola modifica della key attiva non elimina la dipendenza dalle vecchie. Per file JSON locali usare mount sicuro fuori git, permessi minimi, no chiavi in variabili EXPO_PUBLIC, e non consegnare mai file di chiavi tramite assistente/pubblico/issue.

**Security boundary incompleto:** non esiste ancora una cifra applicativa dei dati in `memory_entries`, `consent_grants`, embedding pgvector, recording device, OAuth tokens o backup. La keyring da file è un adapter di sviluppo, NON un secrets vault con audit, rotazione, revoca, ACL e HSM. Senza un layer persistente cifrato, un dump PostgreSQL continua a contenere testo in chiaro. Necessari KMS/Key Vault ↔ OpenBao con identità workload, migrazione dei dati preesistenti, error handling dei key outage, design di ricerca semantica su embedding sensibili, purge di chiavi dopo restore con tombstone. Per fonti: OWASP Cryptographic Storage https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html e Key Management https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html , Microsoft .NET 10 AesGcm https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.-ctor?view=net-10.0 .
