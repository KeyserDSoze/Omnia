# Strategia di test

TDD per ogni comportamento: test che fallisce → implementazione → refactor. Un test non eseguito
non conta come superato.

## Livelli

| Livello | Dove | Cosa copre | Comando |
|---|---|---|---|
| Unit (.NET) | `tests/Tao.Domain.Tests` | dominio, casi d'uso con fake (connettori, oggi, assistente), mapping HTTP Graph/Google e modello AI con `HttpMessageHandler` finti | `dotnet test tests/Tao.Domain.Tests/Tao.Domain.Tests.csproj` |
| Integrazione (.NET) | `tests/Tao.Integration.Tests` | PostgreSQL 17 + pgvector reale con ruoli separati: RLS cross-attore, sigillatura e AAD, state OAuth monouso, export/cancellazione, retention, guardie dei ruoli, contratti API con `WebApplicationFactory` | vedi sotto |
| Unit (web) | `tests/mobile-unit` (Vitest) | logica pura: deck, callback OAuth, client API, filtri timeline, sessioni | `cd src/mobile && npm test` |
| Typecheck | `src/mobile` | TypeScript strict | `npm run typecheck` |
| E2E statico | `src/mobile/e2e/first-run.spec.ts` | build esportata: idratazione senza errori, demo del deck con swipe reali, relay del callback OAuth | `npm run test:e2e` |
| E2E OIDC | workflow `oidc-e2e.yml` | browser reale → Keycloak → API → PostgreSQL | CI |
| E2E gateway | `src/mobile/e2e/gateway.spec.ts`, job CI `gateway` | stack compose completo su `:59800`: login via `/auth`, deck, connessioni, assistente, nota via `/api` | vedi sotto |

## PostgreSQL locale per i test di integrazione

```sh
docker run -d --name tao-test-pg -e POSTGRES_USER=tao -e POSTGRES_PASSWORD=<pw> -e POSTGRES_DB=tao_test \
  -p 15432:5432 pgvector/pgvector:pg17
# creare i ruoli tao_app e tao_retention (NOSUPERUSER NOBYPASSRLS) come nel job "backend" di .github/workflows/ci.yml
export TAO_TEST_DB="Host=127.0.0.1;Port=15432;Database=tao_test;Username=tao;Password=<pw>"
export TAO_TEST_APP_DB="Host=127.0.0.1;Port=15432;Database=tao_test;Username=tao_app;Password=<pw-app>"
export TAO_TEST_RETENTION_DB="Host=127.0.0.1;Port=15432;Database=tao_test;Username=tao_retention;Password=<pw-ret>"
export TAO_TEST_KEYRING_FILE=/percorso/keyring.json   # {"activeKeyId":"k1","keys":{"k1":"<base64 32 byte>"}}
dotnet test tests/Tao.Integration.Tests/Tao.Integration.Tests.csproj
```

## E2E sul gateway

```sh
docker/setup.sh && docker compose --env-file docker/.env -f docker/compose.yml up -d --build
# creare un utente usa-e-getta con kcadm (vedi job "gateway" in .github/workflows/ci.yml)
cd src/mobile && TAO_GATEWAY_E2E=1 TAO_E2E_BASE_URL=http://localhost:59800 \
  TAO_E2E_USER1=<utente> TAO_E2E_PASSWORD=<password> npx playwright test e2e/gateway.spec.ts
```

## Regole

- Ogni nuova tabella personale: test di RLS cross-attore, sigillatura (nessun testo in chiaro nel
  DB), export, cancellazione e retention.
- Ogni chiamata a provider esterni: test con handler finto su URL, metodo, header, body e mapping
  degli errori (nessun corpo del provider nei messaggi).
- Ogni flusso UI importante: Playwright con `testID` stabili; per i gesti usare movimenti del mouse
  realistici (più step), non un singolo salto.
- Niente dati personali reali nei test; utenti, token e contenuti sono sintetici.
