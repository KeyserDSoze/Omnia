# Strategia di test

TDD per ogni comportamento: test che fallisce → implementazione → refactor. Un test non eseguito
non conta come superato.

## Livelli

| Livello | Dove | Cosa copre | Comando |
|---|---|---|---|
| Unit (.NET) | `tests/Tao.Domain.Tests` | dominio (incluse impostazioni e deleghe), casi d'uso con fake (connettori, oggi, impostazioni, assistente), mapping HTTP Graph/Google e modello AI con `HttpMessageHandler` finti | `dotnet test tests/Tao.Domain.Tests/Tao.Domain.Tests.csproj` |
| Integrazione (.NET) | `tests/Tao.Integration.Tests` | PostgreSQL 17 + pgvector reale con ruoli separati: RLS cross-attore, sigillatura e AAD, state OAuth monouso, export/cancellazione, retention, guardie dei ruoli, contratti API con `WebApplicationFactory` (incluse le deleghe che decidono cosa legge l'agente) | vedi sotto |
| Unit (web) | `tests/mobile-unit` (Vitest) | logica pura: configurazione guidata (`setupFlow`), script della chat, deck, callback OAuth, client API, filtri timeline, sessioni | `cd src/mobile && npm test` |
| Typecheck | `src/mobile` | TypeScript strict | `npm run typecheck` |
| E2E statico | `src/mobile/e2e/first-run.spec.ts` | build esportata: chat iniziale, configurazione guidata in demo, idratazione senza errori, demo del deck con swipe reali, relay del callback OAuth | `npm run test:e2e` |
| E2E OIDC | `src/mobile/e2e/auth-sync.spec.ts`, workflow `oidc-e2e.yml` | browser reale → Keycloak → API → PostgreSQL (note, consensi, export, cancellazione, isolamento tra utenti) | CI, o in locale replicando i passi del workflow |
| E2E gateway | `src/mobile/e2e/gateway.spec.ts`, job CI `gateway` | stack compose completo su `:59800`: login dalla chat via `/auth`, configurazione guidata salvata su PostgreSQL, impostazioni e deleghe dopo un reload, deck e assistente nella chat, nota via `/api` | vedi sotto |

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

## E2E OIDC in locale

Il workflow `oidc-e2e.yml` si replica su Windows o Linux con gli stessi passi: PostgreSQL e
Keycloak usa-e-getta (`tao-realm.json`, `TAO_PUBLIC_URL=http://127.0.0.1:19006`), tre utenti
sintetici creati con l'admin API, API su `127.0.0.1:8080`, build statica con
`EXPO_PUBLIC_TAO_OIDC_ISSUER`, `EXPO_PUBLIC_TAO_OIDC_CLIENT_ID`, `EXPO_PUBLIC_TAO_API_URL` e
`TAO_WEB_BASE_PATH=/Tao`, poi `TAO_REAL_OIDC_E2E=1 npx playwright test e2e/auth-sync.spec.ts`.
Due avvertenze:

- la suite rispetta il limite reale di 120 richieste al minuto per utente. Su una macchina veloce
  i test dello stesso utente stanno in meno di un minuto e alcuni ricevono 429. Non alzare il limite:
  rilancia i test interessati da soli (`--grep`);
- alcuni test sui giorni assumono l'orologio del browser in UTC, come sul runner GitHub. In un altro
  fuso usa una config con `timezoneId: 'UTC'`.

## Regole

- Ogni nuova tabella personale: test di RLS cross-attore, sigillatura (nessun testo in chiaro nel
  DB), export, cancellazione e retention.
- Ogni chiamata a provider esterni: test con handler finto su URL, metodo, header, body e mapping
  degli errori (nessun corpo del provider nei messaggi).
- Ogni flusso UI importante: Playwright con `testID` stabili; per i gesti usare movimenti del mouse
  realistici (più step), non un singolo salto.
- Niente dati personali reali nei test; utenti, token e contenuti sono sintetici.
