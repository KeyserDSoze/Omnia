# Sicurezza

Modello: un'installazione personale/di team su un host on-prem, esposta tramite Cloudflare Tunnel.
Questo documento descrive i controlli presenti su `main` e i rischi residui; non è una certificazione.

## Superficie esposta

| Ingresso | Protezione |
|---|---|
| `:59800` (nginx) | legato a `127.0.0.1` di default; header di sicurezza (CSP stretta, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy` con `microphone=(self)` per la dettatura nel browser, camera e geolocalizzazione negate), HSTS solo dietro tunnel HTTPS; console Keycloak `/auth/admin` e realm `master` bloccati (404) |
| `:59801` | console admin Keycloak, solo host |
| `/api/v1/*` | JWT obbligatorio (issuer, audience `tao-api`, firma, scadenza), `sub` e `tenant_id` unici; rate limit per attore e per tenant; budget del body JSON (dimensione, tempo, concorrenza); limiti header Kestrel; `Cache-Control: no-store` |

## Identità e autorizzazione

- Keycloak in modalità produzione, registrazione disattivata, client pubblico `tao-web` con PKCE S256.
- **Accesso con Google/Microsoft** (opzionale, `TAO_SOCIAL_LOGIN`, ADR 0104): con
  `TAO_SOCIAL_SIGNUP=open` (default) il primo accesso crea l'utente in Keycloak con l'e-mail del
  provider e gli accessi successivi lo riconoscono. Se l'e-mail appartiene già a un utente, il
  collegamento di un secondo provider richiede una prova (password o provider già collegato):
  `trustEmail` è disattivato, quindi un'e-mail dichiarata dal provider non basta a prendere un
  account. Con `TAO_SOCIAL_SIGNUP=invite` il flusso `tao-invite-only-first-login` accetta solo utenti
  già esistenti. `GOOGLE_HOSTED_DOMAIN` limita Google a un dominio Workspace; Microsoft usa il tenant
  di `MICROSOFT_TENANT` (default `organizations`). Gli utenti restano solo in Keycloak, nessuna
  tabella utenti Tao. L'API non cambia: accetta solo JWT del realm `tao`.
  `GET /api/login-options` (pubblico) espone solo l'elenco dei provider abilitati.
- Tenant e soggetto arrivano **solo** dal token validato; nessun parametro li sovrascrive.
- Ogni query passa da `PostgresActorScope` (GUC per transazione) e da **FORCE RLS**; il ruolo
  dell'API non è owner, non ha BYPASSRLS né TRUNCATE (verificato all'avvio). Test su PostgreSQL
  reale coprono l'accesso cross-tenant e cross-utente per ogni tabella.

## Segreti e cifratura

- Segreti generati da `docker/setup.*`; `docker/.env` e `docker/secrets/` non sono versionati.
- Busta AES-256-GCM (`TAO1`): DEK casuale per record, KEK dal key ring, AAD = tenant + owner +
  scopo + id riga, quindi un ciphertext spostato in un'altra riga non si apre (test dedicato).
  Sigillati: contenuti delle note, impostazioni e deleghe, verifier PKCE, token OAuth e identità
  degli account collegati.
- I token OAuth non lasciano mai il server: non sono nel browser, nei log o nell'export.

## Connettori

- OAuth con PKCE e client secret lato server; state casuale da 256 bit salvato come SHA-256,
  monouso, 10 minuti, legato all'utente che l'ha generato (un callback di un altro utente fallisce).
- Il popup consegna `{state, code}` solo alla stessa origine (BroadcastChannel/postMessage con
  controllo di origine e di state); il codice viene rimosso dall'URL e non viene salvato.
- Lettura di default; l'unica scrittura richiede conferma per singola azione e produce audit.
- **Deleghe per account** (impostazioni): il server le applica prima di chiamare il provider. Le
  categorie non delegate non vengono richieste; il completamento senza delega `tasks.complete` o in
  modalità `read_only` risponde 403 `delegation_required` senza contattare il provider. Le
  impostazioni sono validate nel dominio (orari, etichette senza caratteri di controllo, al massimo
  10 account, deleghe ammesse per provider) e possono riferirsi solo ad account dello stesso utente.
- I link provenienti dai provider sono aperti solo se `https:` senza credenziali.
- I corpi di errore dei provider non vengono mai mostrati o loggati; i log `HttpClient` sono a
  livello Warning per non registrare URL con identificativi.

## AI

- Consenso dedicato, dati minimizzati, contenuto delle carte dichiarato non affidabile,
  output vincolato a JSON con riferimenti noti, nessuna esecuzione automatica.
- Endpoint del modello HTTPS (HTTP solo loopback/host privato); errori senza dettagli del provider.

## Supply chain

Lockfile npm e NuGet con restore bloccato, audit HIGH/CRITICAL in CI, CodeQL, Dependabot mensile,
SDK .NET e immagini Docker fissati per digest.

## Rischi residui e azioni consigliate

| Rischio | Mitigazione suggerita |
|---|---|
| Host compromesso = key ring + DB | disco cifrato, utente dedicato, backup cifrati, aggiornamenti |
| Account Keycloak con password debole | MFA/OTP in Keycloak, Cloudflare Access davanti al tunnel |
| Account Google/Microsoft dell'utente compromesso | con l'accesso social chi controlla l'account esterno entra in Tao: tenere MFA sul provider, restringere con `GOOGLE_HOSTED_DOMAIN`/`MICROSOFT_TENANT` o `TAO_SOCIAL_SIGNUP=invite`, o lasciare `TAO_SOCIAL_LOGIN` vuoto |
| Furto del token di sessione nel browser | token solo in memoria, scadenze brevi, CSP; non usare estensioni non fidate |
| Prompt injection da email/chat | output solo consultivo; nessuna azione automatica (mantenere) |
| Nessun backup automatico | runbook in epic #221 |
| CI GitHub non eseguita (budget Actions esaurito) | ripristinare il budget e verificare i workflow su `main` |

Segnalazioni di vulnerabilità: aprire una issue privata/advisory su GitHub senza dati reali.
