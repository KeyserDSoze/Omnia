# Omnia — Test strategy e Evidence Gates (TDD)

## Regola di lavoro

Per ogni feature: (1) test che fallisce per il comportamento richiesto; (2) implementazione minima; (3) refactor e regressioni; (4) esecuzione reale con SHA/URL Actions. Mai chiudere una issue solo perché il test è stato scritto o un mock passa. Ogni boundary esterno ha **test di contratto** oltre ai fake usati nello unit layer.

## Pyramid

| Layer | Test runner | Esempio | Frequenza |
|---|---|---|---|
| Domain/application | xUnit + Vitest | normalizzazione, idempotenza, tag, session state, policy decision | ogni commit |
| API + PostgreSQL | xUnit integration/Postgres pgvector | migrations, JWT 401/403, RLS, write/read, delete | ogni commit |
| Provider contracts | xUnit + disposable Docker dependencies | MinIO/Azure IObjectStore, RabbitMQ/Service Bus IEventBus, Ollama/Azure embeddings | ogni commit OSS; Azure staging opt-in |
| True Browser E2E | Playwright Chromium Web under `/Omnia/` | login PKCE reale, React→HTTP→PostgreSQL, theme, capture, consent revoke | ogni commit core |
| Native E2E | Android emulator/iOS simulator + device test | secure token, microphone permission, offline capture, deep link | prerelease e PR mobile |
| AI eval | synthetic golden corpus/red-team | grounded recap, hallucinations, model/summary provenance, prompt injection | cambi modello/feature AI |
| Nonfunctional | k6/chaos/SCA/secret scanning | quotas, restore, massive imports, kill switch, security | staging + release |

## Component truthfulness

- Fake/mocks testano algoritmi e fallimenti, **non** attestano disponibilità cloud o provider OAuth.
- `IEmbeddingProvider` `none` deve dare *pending/unembedded*, mai un vettore inventato; per embeddings testare l'insert PostgreSQL con vettore non nullo e modello/dimension corretto.
- Test del mondo reale devono usare access token emessi da un provider di prova, un database effettivo e l'app web compilata; il test di pagina statica è un controllo diverso.
- Per microfono/chiamate: permission/state e fallback reali, non garantire intercettazione audio non disponibile.
- Per MCP: simulate server malicious, tool description injection, SSRF, token audience, scope escalation, data poisoning, wrong tenants.
- Fixtures solo sintetiche, password generate per CI, clean-up idempotente, segreti mascherati, test reports associati a commit SHA.

## Required gates per stage

- **M0:** .NET/Expo locked dependencies, secret/SCA scans, compose config, smoke pgvector, xUnit, Vitest, Playwright static.
- **M1:** real OIDC/PCKE browser→API→PostgreSQL, cross-tenant denied, CORS/CSP, consent fail closed, logout + token revoke, data export/delete.
- **M2:** media upload with object store, STT contract, timestamps and provenance, async retry/DLQ, DB RLS, pgvector search, Azure staging IaC review.
- **M3:** connector OAuth and webhook contract, MCP sandbox red-team, AI quality eval and native builds tested on emulator/simulator.
- **M4:** read-only default actions, user approval gated, replay protection, billing sandbox/idempotency, quota and invoice handling.

## CI evidence policy

Each issue links a commit with passing required jobs and relevant logs. Mark as 'implementation in progress' rather than 'done' if any gate is untested due to missing real cloud credentials, device or provider API. Smoke/preview environments may use temporary accounts but never personal data. 

## RLS integration gate (#30)

The CI runs xUnit against disposable pgvector PostgreSQL with a randomized schema-owner password and a separate randomized `omnia_app` password. Tests use the real non-owner connection for all repository queries, never the migration admin. `RlsIsolationTests` deliberately sends SELECT without tenant WHERE and checks fail-closed SELECT/INSERT/UPDATE, no bypass of `row_security`, and no transaction actor-context leakage across a single pooled connection. The full OIDC browser workflow provisions the same app role and confirms JWT → consent → persisted note and cross-owner isolation. An API process with privileged runtime credentials must refuse to start.

## GitHub Pages artifact handoff reliability (#142, 2026-11-20)

Failure injection evidenced by [run 44824935287](https://github.com/KeyserDSoze/Omnia/actions/runs/44824935287):
upload succeeded, but deploy immediately observed zero artifacts; rerunning the
whole job uploaded a second same-named `github-pages` artifact, making deploy
fail with `Multiple artifacts named ... count is 2`. The Expo build was
successful; this is an artifact metadata/deploy race.

Pages now uploads under `omnia-pages-<run_id>-<run_attempt>`, and the
deploy action targets exactly that name. With read-only Actions permission,
a bounded wait checks that exactly one unexpired artifact is indexed by
GitHub before deployment. Zero until timeout or more than one is
fail-closed. No secret is displayed and no publication is authorized when
Pages is disabled. The workflow is still tested by real GitHub Actions,
not a mocked local runner. Consult issue #142 for observed GREEN evidence;
do not infer deployment health solely from a successful static export.

- Official upload action: https://github.com/actions/upload-pages-artifact
- Official deploy action: https://github.com/actions/deploy-pages
- GitHub Pages custom workflows:
  https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages

## Negative security regression: dual claim physical PITR (#141 / ADR 0042)

Run the CI-only shell gate after the separate THIRD and FOURTH
PITR tests. A passing test intentionally observes **both
post-target claims lost and re-admitted**; the drill CLI
returns exit **42**, not zero, with a fixed no-release
sentinel. A missing sentinel, unexpected success or any
other error must fail the workflow. This captures a known
common-mode vulnerability without mistaking a successful
replay for a safe recovery. All source actor/tenant values
are hardcoded synthetic test fixtures; neither the primary
personal data DB nor the REST endpoint is modified.

### Pages artifact-service consistency follow-up (#142)

The initial single-job REST polling was insufficient. Run
[44828446417](https://github.com/KeyserDSoze/Omnia/actions/runs/44828446417)
showed REST `Pages artifact visible` while
`actions/deploy-pages@v4` immediately observed 0 artifacts through
the internal `@actions/artifact` artifact-service API. The workflow
therefore separates `build` and `deploy` jobs, ensures upload
finishes before deploy job starts, passes the build job's exact artifact
name through a job output (important for partial reruns), and applies
a bounded metadata propagation grace period. Permissions are split:
build has read access; deployment alone has `pages:write` and
`id-token:write`. This is not a guarantee of GitHub infrastructure
availability; verify the REAL latest Actions run before closing #142.
Source: https://github.com/actions/deploy-pages/blob/v4/src/internal/api-client.js

## #143 — Provider-neutral WORM evidence contract (ADR 0043)

`ImmutableAttemptEvidenceContractTests` esercita ricevute
sintetiche canonical JSON + HMAC dello scope + un secondo
HMAC di evidence, immutable version ID esterno, storage
identity, retained-until e metadata di lock. Rifiuta
strictly empty/forged/mutable/expired/mismatched/missing
o I/O error. Un successo è **solo contract test con
reader mock**: real S3 Object Lock e Azure Blob immutable
policy/identity/version pin NON sono qui integrazione,
e nessun permission token di cancellazione esiste.

### RED/GREEN verificata per immutable attempt version pinning (#143)

- [CI RED 44882236030](https://github.com/KeyserDSoze/Omnia/actions/runs/44882236030): compilazione senza modello/provider contract;
- [CI RED 44882440793](https://github.com/KeyserDSoze/Omnia/actions/runs/44882440793): 1 di 80 test fallito per versioni `latest` e `null`;
- [CI GREEN 44882456199](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456199): test xUnit/restore/PITR/Docker e mobile verde sul commit `390331a4`;
- [OIDC E2E 44882456209](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456209) e [Pages 44882456200](https://github.com/KeyserDSoze/Omnia/actions/runs/44882456200) green stesso SHA. Questa è evidenza solo del **contratto con stub**, non di uno storage WORM reale.

## AWS SDK S3 immutable object version provider rehearsal (#145)

Il nuovo progetto xUnit `tests/Omnia.S3ObjectLock.Tests`
esercita l'API S3 reale di un OSS temporaneo:
bucket con Object Lock, versione COMPLIANCE,
GET con VersionId, GetObjectRetention stessa versione,
GOVERNANCE/unlocked/alias/foreign pin negati,
tentativo DELETE versione COMPLIANCE negato.
I log NON devono stampare segreti o contenuti.
SeaweedFS usa lo stesso runner della primaria;
l'assenza di offsite/WORM effettivo e di test Azure
rimane esplicitamente nelle issue aperte.

### Evidenze TDD e compatibilità S3 Object Lock (#145)

Il test-first ha prodotto [RED CI 44884847236](https://github.com/KeyserDSoze/Omnia/actions/runs/44884847236) (provider S3 non ancora implementato) e [RED CI 44884878720](https://github.com/KeyserDSoze/Omnia/actions/runs/44884878720) (enum AWS SDK PUT da correggere). Dopo test-first, adapter e aggiustamenti del bootstrap S3 CI con uid/gid reale e file privato `0400`, [GREEN CI 44885831789](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831789) su `649e12ab` ha eseguito **2/2 test REALI SeaweedFS**, **19/19 Domain** e **80/80 Integration**, PITR fisici, Docker/mobile; [OIDC 44885831790](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831790) e [Pages 44885831828](https://github.com/KeyserDSoze/Omnia/actions/runs/44885831828) GREEN. Il test provider verifica una semantica S3 reale ma non può attestare l'indipendenza dallo stesso runner, né validità giuridica della retention.

## Azure Blob SDK version-level WORM contract tests (#146 / ADR 0045)

Il progetto `tests/Omnia.AzureBlobWorm.Tests` esercita il
client reale Azure.Storage.Blobs usando un HTTP transport
fake: HEAD+GET pinned with `versionid`, risposta con
policy Locked vs Unlocked/missing/expired, mismatch
VersionId o payload/ETag, e failure HTTP, senza passare
da una istanza Azure. Azurite NON deve essere usato
come prova di version-level WORM. Test reale in tenant
Azure staging con Locked immutability + Managed Identity
e 403 DELETE sono ancora da implementare (#144).
