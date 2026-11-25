# Tao — regole del repository

**Repository:** https://github.com/KeyserDSoze/Tao · branch di lavoro: `main` · mai force push.

## Prima di cambiare codice

1. Leggi [README.md](README.md), [docs/architecture.md](docs/architecture.md),
   [docs/roadmap.md](docs/roadmap.md) e le ADR pertinenti in `adr/` (le nuove partono da 0098).
2. Funzionalità non banali: issue sotto l'epic giusta (vedi [docs/backlog.md](docs/backlog.md));
   decisioni architetturali in una nuova ADR.
3. TDD: prima un test che fallisce per il comportamento, poi l'implementazione (RED → GREEN → REFACTOR).
4. Esegui davvero xUnit, PostgreSQL, typecheck, Vitest e Playwright pertinenti; non dichiarare
   mai superati test non eseguiti. Se la CI GitHub non gira, dillo esplicitamente.

## Confini

- `Tao.Domain`: invarianti e tipi; nessun SDK o I/O.
- `Tao.Application`: casi d'uso e porte; niente Npgsql/HTTP.
- `Tao.Infrastructure`: PostgreSQL (migrazioni append-only `NNNN_name.sql`, mai modificare una
  migrazione già applicata), connettori HTTP, modello AI, cifratura.
- `Tao.Api`: REST `/api/v1`, OIDC/JWT, limiti, DI. `src/mobile`: UI Expo che deve funzionare
  sia sul gateway (stessa origine) sia come export statico su Pages (`/Tao`).
- `docker/`: stack on-prem (nginx :59800 → `/`, `/api`, `/auth`).

## Invarianti di sicurezza e privacy (obbligatori)

- Tenant e soggetto vengono **solo** dal JWT validato (`tenant_id`, `sub`); mai da body, header o query.
- Ogni tabella con dati personali è actor-scoped con **FORCE RLS** e usa `PostgresActorScope`;
  le nuove tabelle vanno aggiunte a `PostgresRuntimeGuard.PersonalTables`, all'export, alla
  cancellazione e alla retention, con test su PostgreSQL reale (incluso accesso cross-actor).
- Contenuti personali e credenziali a riposo sono **sigillati** con `IEnvelopeEncryption`
  (AES-256-GCM, AAD = tenant + owner + purpose + id della riga). Nessun fallback in chiaro.
- Token OAuth: solo lato server, mai nel browser, mai nei log, mai nell'export.
- Connettori: lettura di default; ogni scrittura su un provider richiede conferma esplicita
  dell'utente per quella singola azione e una riga di audit. Mai invio automatico di messaggi.
- AI: solo con consenso dedicato (`assistant.briefing`), dati minimizzati, contenuti delle carte
  trattati come input non affidabile, output etichettato come AI e mai eseguito automaticamente.
  Non presentare euristiche deterministiche (tag, priorità) come "AI".
- Risposte `/api/v1`: `Cache-Control: no-store`, errori con codici stabili senza contenuti del provider.
- Niente credenziali, token o dati personali reali nel repository; GitHub Pages è solo demo statica.
- Niente registrazioni non consensuali, sorveglianza passiva o accesso a dati di terzi senza consenso.

## Comandi

```sh
cd src/mobile && npm ci && npm run typecheck && npm test && npm run build:web && npm run test:e2e
dotnet test tests/Tao.Domain.Tests/Tao.Domain.Tests.csproj
# PostgreSQL reale: TAO_TEST_DB, TAO_TEST_APP_DB, TAO_TEST_RETENTION_DB, TAO_TEST_KEYRING_FILE
dotnet test tests/Tao.Integration.Tests/Tao.Integration.Tests.csproj
docker compose --env-file docker/.env -f docker/compose.yml up -d --build
```

## Dipendenze

- npm: `src/mobile/package-lock.json` + `npm ci`; niente `--force` / `--legacy-peer-deps`.
  I pacchetti gestiti da Expo si aggiornano con l'SDK.
- NuGet: versioni centrali in `Directory.Packages.props`, `packages.lock.json` per progetto,
  restore in locked mode con `CI=true`. Dopo un cambio di riferimenti rigenera i lock e
  verifica `dotnet restore --locked-mode` per ogni progetto **e** la build `docker/Dockerfile.api`.
- SDK .NET fissato da `global.json`; immagini Docker con digest.
- Gate in CI: audit npm/NuGet HIGH/CRITICAL, CodeQL, Dependabot mensile raggruppato.
