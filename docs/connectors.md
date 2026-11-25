# Connettori di lavoro: Microsoft 365 e Google Workspace

Tao legge i segnali della giornata **al momento** (non copia caselle o calendari) e conserva solo
le tue decisioni sulle carte. L'unica scrittura possibile è completare un'attività, e solo con la
spunta esplicita sulla carta. Decisione architetturale: [ADR 0099](../adr/0099-work-connectors-and-daily-deck.md).

## Cosa viene letto

| Provider | Carte | API |
|---|---|---|
| Microsoft 365 | riunioni del giorno (esclusi annullati/rifiutati), posta in arrivo non letta o importante/contrassegnata, attività To Do non completate in scadenza, ultimi messaggi di chat Teams non letti scritti da altri | Graph v1.0: `/me/calendarView`, `/me/mailFolders/inbox/messages`, `/me/todo/lists/*/tasks`, `/me/chats?$expand=lastMessagePreview` |
| Google Workspace | eventi del calendario principale, Gmail in arrivo non letta/importante/speciale (solo metadati e snippet), Google Tasks non completate in scadenza | Calendar v3, Gmail v1 (`format=metadata`), Tasks v1 |

Unica scrittura: `PATCH` dello stato a *completed* di un'attività To Do / Google Tasks.

## Registrazione Microsoft Entra

1. [Entra admin center](https://entra.microsoft.com) → *App registrations* → *New registration*.
   - *Supported account types*: account di qualsiasi organizzazione (con `MICROSOFT_TENANT=organizations`)
     oppure solo il tuo tenant (con `MICROSOFT_TENANT=<tenant id>`).
   - *Redirect URI*: piattaforma **Web**, `${TAO_PUBLIC_URL}/connections/callback`
     (es. `https://tao.example.com/connections/callback`, in locale `http://localhost:59800/connections/callback`).
2. *Certificates & secrets* → *New client secret* → `MICROSOFT_CLIENT_SECRET` (annota la scadenza e ruotalo).
3. *API permissions* → Microsoft Graph → **Delegated**: `User.Read`, `Mail.Read`, `Calendars.Read`,
   `Tasks.ReadWrite`, `Chat.Read`, `offline_access`, `openid`, `profile`, `email`.
   Se il tenant limita il consenso degli utenti, concedi l'*admin consent*.
4. *Application (client) ID* → `MICROSOFT_CLIENT_ID`.

## Registrazione Google Cloud

1. [Google Cloud console](https://console.cloud.google.com) → nuovo progetto → *APIs & Services* →
   abilita **Google Calendar API**, **Gmail API**, **Google Tasks API**.
2. *OAuth consent screen*: *Internal* se usi Google Workspace della tua organizzazione; *External*
   altrimenti (in modalità *Testing* solo gli utenti di test possono collegarsi e i refresh token
   scadono dopo 7 giorni; `gmail.readonly` è uno scope *restricted* che richiede verifica per la produzione).
   Scope: `openid`, `email`, `profile`, `.../auth/calendar.readonly`, `.../auth/gmail.readonly`, `.../auth/tasks`.
3. *Credentials* → *Create OAuth client ID* → **Web application** → *Authorized redirect URI*
   `${TAO_PUBLIC_URL}/connections/callback`.
4. `GOOGLE_CLIENT_ID` e `GOOGLE_CLIENT_SECRET` in `docker/.env`, poi `docker compose ... up -d`.

## Flusso OAuth

```mermaid
sequenceDiagram
  participant W as Finestra Tao
  participant P as Popup
  participant A as API Tao
  participant I as Microsoft/Google
  W->>A: POST /api/v1/connectors/{provider}/authorize (Bearer)
  A->>A: state e verifier casuali; salva SHA-256(state) + verifier sigillato (10 min)
  A-->>W: authorizationUrl (PKCE S256)
  W->>P: apre il popup sull'authorizationUrl
  P->>I: login e consenso
  I-->>P: /connections/callback?code&state
  P-->>W: BroadcastChannel / postMessage stessa origine (nessuno storage)
  W->>A: POST /api/v1/connectors/complete {state, code} (Bearer)
  A->>A: state monouso, legato all'utente che l'ha creato
  A->>I: scambio code + client secret + verifier
  A->>A: token e identità sigillati (AES-256-GCM, AAD tenant/owner/account)
```

- I token non raggiungono mai il browser, non sono nei log e non sono nell'export.
- Refresh automatico 2 minuti prima della scadenza, sotto lock di riga; Google non ruota il
  refresh token, Tao conserva quello precedente.
- Scollegamento: Tao cancella i token e le decisioni dell'account; per Google revoca anche il
  grant, per Microsoft rimuovi il consenso da [myapps.microsoft.com](https://myapps.microsoft.com).

## Cosa viene salvato

| Tabella | Contenuto | Durata |
|---|---|---|
| `connector_authorizations` | hash dello state, verifier PKCE sigillato | 10 minuti (poi purge) |
| `connector_accounts` | provider, id esterno, credenziali + identità sigillate | finché non scolleghi o cancelli i dati |
| `triage_decisions` | chiave della carta (`provider:account:tipo:id`), decisione, rinvio | 30 giorni |
| `connector_actions` | audit delle scritture sul provider (azione, esito, ora) | 180 giorni |

Tutte con FORCE RLS per tenant e utente, incluse in export e cancellazione.

## API

| Metodo | Percorso | Note |
|---|---|---|
| GET | `/api/v1/connectors` | provider (configurato, capacità) e account collegati |
| POST | `/api/v1/connectors/{microsoft\|google}/authorize` | body `{}` → `{ authorizationUrl }` |
| POST | `/api/v1/connectors/complete` | `{ state, code }` → account |
| DELETE | `/api/v1/connectors/accounts/{id}` | 204 / 404 |
| GET | `/api/v1/today?from&to` | finestra massima 72 h; carte ordinate + stato per account |
| POST | `/api/v1/today/decisions` | `{ key, decision: done\|not_done\|later\|dismissed, snoozeMinutes?, applyToSource }` |

Errori (`{ "error": "<codice>" }`): `connector_not_configured` (409), `reconnect_required` (409),
`unknown_state` / `expired_state` / `invalid_request` (400), `authorization_failed`,
`provider_error`, `rate_limited`, `apply_failed` (502), `apply_not_supported`, `invalid_item`,
`invalid_decision`, `invalid_snooze`, `invalid_window` (400), `not_found` (404).
Un permesso mancante su una categoria (es. chat) nasconde solo quella categoria; un account non
raggiungibile appare come `unavailable` senza bloccare gli altri.

## Priorità delle carte (deterministica)

| Carta | Punteggio |
|---|---|
| Attività scaduta | 100 |
| Attività in scadenza entro 24 h | 90 |
| Riunione in corso | 88 |
| Riunione entro 2 h | 85 |
| Email importante | 80 |
| Chat non letta | 75 |
| Email non letta | 70 |
| Attività importante / normale | 65 / 60 |
| Riunione più tardi | 50 |
| Email letta | 40 |
| Riunione terminata | 10 |
