# Omnia — Full product feature backlog

> Aggiornamento 2026-11-17 · branch `main` · 14 epics + **116** feature child (130 issue aperte di progetto); issue #35 chiusa come duplicata di #34. La creazione di una issue **non significa** implementazione. La foundation v0.1 esiste e la sua CI era verde al momento della progettazione; gli step successivi sono ancora da sviluppare e verificare.

## Convenzioni di gestione

- Ogni epic e ogni child porta la label `feature`. Altre label: `epic`, `area:*`, `priority:p0|p1|p2`, `phase:m0|m1|m2|m3|m4|m5`.
- **P0:** sicurezza, dati, auth e gate bloccanti; **P1:** percorso prodotto completo; **P2:** ottimizzazione e go-to-market.
- **M0 (quality/privacy discovery):** prerequisiti e blocchi su dati reali; **M1 (secure personal timeline):** login, capture→sync→view; **M2 (memory/media + Azure staging):** struttura, audio, vettori, cloud; **M3 (connections & intelligence):** connettori e recap con evidenze; **M4 (agency + SaaS):** azioni autorizzate, fatturazione, automazioni; **M5 (enterprise):** residenza dati e controllo per aziende.
- Gli *epic* rappresentano gruppi, non compiti atomici; le child issue sono slice verticali implementabili. La prima issue di una fase non attiva automaticamente i servizi cloud.
- GitHub Projects/Milestones formali **non configurati** con questi strumenti. Le milestone sono espresse come label `phase:*` e mappate qui.

## Epic → feature

| Area | Epic | Fase | Feature figlie | Issue child |
|---|---|---|---:|---|
| Identity & auth | [#1](https://github.com/KeyserDSoze/Omnia/issues/1) | M1 | 7 | [#39](https://github.com/KeyserDSoze/Omnia/issues/39) · [#40](https://github.com/KeyserDSoze/Omnia/issues/40) · [#41](https://github.com/KeyserDSoze/Omnia/issues/41) · [#42](https://github.com/KeyserDSoze/Omnia/issues/42) · [#43](https://github.com/KeyserDSoze/Omnia/issues/43) · [#44](https://github.com/KeyserDSoze/Omnia/issues/44) · [#45](https://github.com/KeyserDSoze/Omnia/issues/45) |
| Privacy, consent & security | [#2](https://github.com/KeyserDSoze/Omnia/issues/2) | M0 | 9 | [#29](https://github.com/KeyserDSoze/Omnia/issues/29) · [#30](https://github.com/KeyserDSoze/Omnia/issues/30) · [#31](https://github.com/KeyserDSoze/Omnia/issues/31) · [#32](https://github.com/KeyserDSoze/Omnia/issues/32) · [#33](https://github.com/KeyserDSoze/Omnia/issues/33) · [#34](https://github.com/KeyserDSoze/Omnia/issues/34) · [#36](https://github.com/KeyserDSoze/Omnia/issues/36) · [#37](https://github.com/KeyserDSoze/Omnia/issues/37) · [#38](https://github.com/KeyserDSoze/Omnia/issues/38) |
| Capture & media | [#3](https://github.com/KeyserDSoze/Omnia/issues/3) | M2 | 6 | [#46](https://github.com/KeyserDSoze/Omnia/issues/46) · [#47](https://github.com/KeyserDSoze/Omnia/issues/47) · [#48](https://github.com/KeyserDSoze/Omnia/issues/48) · [#49](https://github.com/KeyserDSoze/Omnia/issues/49) · [#50](https://github.com/KeyserDSoze/Omnia/issues/50) · [#124](https://github.com/KeyserDSoze/Omnia/issues/124) |
| Connectors & MCP | [#4](https://github.com/KeyserDSoze/Omnia/issues/4) | M3 | 11 | [#51](https://github.com/KeyserDSoze/Omnia/issues/51) · [#52](https://github.com/KeyserDSoze/Omnia/issues/52) · [#53](https://github.com/KeyserDSoze/Omnia/issues/53) · [#54](https://github.com/KeyserDSoze/Omnia/issues/54) · [#55](https://github.com/KeyserDSoze/Omnia/issues/55) · [#56](https://github.com/KeyserDSoze/Omnia/issues/56) · [#57](https://github.com/KeyserDSoze/Omnia/issues/57) · [#58](https://github.com/KeyserDSoze/Omnia/issues/58) · [#121](https://github.com/KeyserDSoze/Omnia/issues/121) · [#122](https://github.com/KeyserDSoze/Omnia/issues/122) · [#123](https://github.com/KeyserDSoze/Omnia/issues/123) |
| Azure & provider parity | [#5](https://github.com/KeyserDSoze/Omnia/issues/5) | M2 | 8 | [#59](https://github.com/KeyserDSoze/Omnia/issues/59) · [#60](https://github.com/KeyserDSoze/Omnia/issues/60) · [#61](https://github.com/KeyserDSoze/Omnia/issues/61) · [#62](https://github.com/KeyserDSoze/Omnia/issues/62) · [#63](https://github.com/KeyserDSoze/Omnia/issues/63) · [#64](https://github.com/KeyserDSoze/Omnia/issues/64) · [#65](https://github.com/KeyserDSoze/Omnia/issues/65) · [#66](https://github.com/KeyserDSoze/Omnia/issues/66) |
| AI & intelligence | [#6](https://github.com/KeyserDSoze/Omnia/issues/6) | M3 | 10 | [#83](https://github.com/KeyserDSoze/Omnia/issues/83) · [#84](https://github.com/KeyserDSoze/Omnia/issues/84) · [#85](https://github.com/KeyserDSoze/Omnia/issues/85) · [#86](https://github.com/KeyserDSoze/Omnia/issues/86) · [#87](https://github.com/KeyserDSoze/Omnia/issues/87) · [#88](https://github.com/KeyserDSoze/Omnia/issues/88) · [#89](https://github.com/KeyserDSoze/Omnia/issues/89) · [#90](https://github.com/KeyserDSoze/Omnia/issues/90) · [#127](https://github.com/KeyserDSoze/Omnia/issues/127) · [#128](https://github.com/KeyserDSoze/Omnia/issues/128) |
| UX / cross-platform | [#7](https://github.com/KeyserDSoze/Omnia/issues/7) | M1 | 9 | [#75](https://github.com/KeyserDSoze/Omnia/issues/75) · [#76](https://github.com/KeyserDSoze/Omnia/issues/76) · [#77](https://github.com/KeyserDSoze/Omnia/issues/77) · [#78](https://github.com/KeyserDSoze/Omnia/issues/78) · [#79](https://github.com/KeyserDSoze/Omnia/issues/79) · [#80](https://github.com/KeyserDSoze/Omnia/issues/80) · [#81](https://github.com/KeyserDSoze/Omnia/issues/81) · [#82](https://github.com/KeyserDSoze/Omnia/issues/82) · [#126](https://github.com/KeyserDSoze/Omnia/issues/126) |
| Memory & knowledge graph | [#8](https://github.com/KeyserDSoze/Omnia/issues/8) | M2 | 10 | [#67](https://github.com/KeyserDSoze/Omnia/issues/67) · [#68](https://github.com/KeyserDSoze/Omnia/issues/68) · [#69](https://github.com/KeyserDSoze/Omnia/issues/69) · [#70](https://github.com/KeyserDSoze/Omnia/issues/70) · [#71](https://github.com/KeyserDSoze/Omnia/issues/71) · [#72](https://github.com/KeyserDSoze/Omnia/issues/72) · [#73](https://github.com/KeyserDSoze/Omnia/issues/73) · [#74](https://github.com/KeyserDSoze/Omnia/issues/74) · [#125](https://github.com/KeyserDSoze/Omnia/issues/125) · [#129](https://github.com/KeyserDSoze/Omnia/issues/129) |
| SaaS / billing | [#9](https://github.com/KeyserDSoze/Omnia/issues/9) | M4 | 8 | [#99](https://github.com/KeyserDSoze/Omnia/issues/99) · [#100](https://github.com/KeyserDSoze/Omnia/issues/100) · [#101](https://github.com/KeyserDSoze/Omnia/issues/101) · [#102](https://github.com/KeyserDSoze/Omnia/issues/102) · [#103](https://github.com/KeyserDSoze/Omnia/issues/103) · [#104](https://github.com/KeyserDSoze/Omnia/issues/104) · [#105](https://github.com/KeyserDSoze/Omnia/issues/105) · [#106](https://github.com/KeyserDSoze/Omnia/issues/106) |
| Quality & TDD | [#10](https://github.com/KeyserDSoze/Omnia/issues/10) | M0 | 9 | [#15](https://github.com/KeyserDSoze/Omnia/issues/15) · [#16](https://github.com/KeyserDSoze/Omnia/issues/16) · [#17](https://github.com/KeyserDSoze/Omnia/issues/17) · [#18](https://github.com/KeyserDSoze/Omnia/issues/18) · [#19](https://github.com/KeyserDSoze/Omnia/issues/19) · [#20](https://github.com/KeyserDSoze/Omnia/issues/20) · [#21](https://github.com/KeyserDSoze/Omnia/issues/21) · [#22](https://github.com/KeyserDSoze/Omnia/issues/22) · [#130](https://github.com/KeyserDSoze/Omnia/issues/130) |
| SRE / observability | [#11](https://github.com/KeyserDSoze/Omnia/issues/11) | M2 | 7 | [#107](https://github.com/KeyserDSoze/Omnia/issues/107) · [#108](https://github.com/KeyserDSoze/Omnia/issues/108) · [#109](https://github.com/KeyserDSoze/Omnia/issues/109) · [#110](https://github.com/KeyserDSoze/Omnia/issues/110) · [#111](https://github.com/KeyserDSoze/Omnia/issues/111) · [#112](https://github.com/KeyserDSoze/Omnia/issues/112) · [#113](https://github.com/KeyserDSoze/Omnia/issues/113) |
| Native distribution | [#12](https://github.com/KeyserDSoze/Omnia/issues/12) | M3 | 7 | [#114](https://github.com/KeyserDSoze/Omnia/issues/114) · [#115](https://github.com/KeyserDSoze/Omnia/issues/115) · [#116](https://github.com/KeyserDSoze/Omnia/issues/116) · [#117](https://github.com/KeyserDSoze/Omnia/issues/117) · [#118](https://github.com/KeyserDSoze/Omnia/issues/118) · [#119](https://github.com/KeyserDSoze/Omnia/issues/119) · [#120](https://github.com/KeyserDSoze/Omnia/issues/120) |
| Product / discovery | [#13](https://github.com/KeyserDSoze/Omnia/issues/13) | M0 | 7 | [#23](https://github.com/KeyserDSoze/Omnia/issues/23) · [#24](https://github.com/KeyserDSoze/Omnia/issues/24) · [#25](https://github.com/KeyserDSoze/Omnia/issues/25) · [#26](https://github.com/KeyserDSoze/Omnia/issues/26) · [#27](https://github.com/KeyserDSoze/Omnia/issues/27) · [#28](https://github.com/KeyserDSoze/Omnia/issues/28) · [#131](https://github.com/KeyserDSoze/Omnia/issues/131) |
| Agents & agency | [#14](https://github.com/KeyserDSoze/Omnia/issues/14) | M4 | 8 | [#91](https://github.com/KeyserDSoze/Omnia/issues/91) · [#92](https://github.com/KeyserDSoze/Omnia/issues/92) · [#93](https://github.com/KeyserDSoze/Omnia/issues/93) · [#94](https://github.com/KeyserDSoze/Omnia/issues/94) · [#95](https://github.com/KeyserDSoze/Omnia/issues/95) · [#96](https://github.com/KeyserDSoze/Omnia/issues/96) · [#97](https://github.com/KeyserDSoze/Omnia/issues/97) · [#98](https://github.com/KeyserDSoze/Omnia/issues/98) |

## Dependency DAG e ordine consigliato (prima esperienza utilizzabile)

```text
M0  #23 PRD ─┬─ #26 DPIA/AI Act ─┐
              ├─ #29 consent ledger ├─ #39 OIDC web ─ #41 API sync ─ #78 quick capture
              ├─ #34 threat model ──┤          │                 └─ #79 timeline
              └─ #15 lockfile ──────┤          └─ #43 RBAC ─ #30 PostgreSQL RLS
                   #16 test compose └─ #17 true browser+API+Postgres gate
M2  #67 canonical schema ─ #68 provenance ─ #70 enrichment ─ #71 embeddings ─ #72 search
    #46 local audio ─ #47 upload/objects ─ #48 transcription ─ #85 daily recap
    #59 VNet ─ #60 ACA+ACR ─ #61 Azure PG ─ #62/63 provider parity
M3  #51 connector SDK ─ #52 MCP discovery ─ #53 sandbox ─ #54/55/56/58 sources
    #73 entity resolution ─ #74 threads ─ #84 RAG ─ #85 recap ─ #86 opt-in reflection
M4  #91 policy engine ─ #93 approval ─ #94 executor ─ #95 draft/send ─ #96 schedule
    #99 tenant lifecycle ─ #100 plan ─ #101 billing ─ #102 usage enforcement
```

**Release gates non negoziabili:** test E2E JWT/PKCE realmente autenticato; no cross-tenant; consenso e revoca efficaci; export/delete/raw+derived; recording con informativa e permessi; model injection mitigata; segreti/PII fuori Git/log; provider cloud deploy verificato. Non usare dati reali prima che siano chiuse le feature privacy pertinenti.

## Come trattare una feature

1. Leggere epic, issue, ADR, source contract e rischi terzi. Definire esplicitamente out-of-scope e dipendenze.
2. Scrivere il test fallente rilevante (**RED**) — xUnit, DB/Testcontainers, Playwright, device o contratti provider.
3. Implementare la vertical slice minima (**GREEN**) con Domain → Application → Infrastructure → API → Expo; niente mock al posto di confini reali.
4. Rifattorizzare, aggiungere minacce/telemetria e documentazione, migrazione e backward compat se cambiano dati.
5. Lanciare la pipeline reale; collegare SHA/URL del run nell'issue. Solo allora chiudere la feature. Nessun claim non provato.

## Spaziatura delle milestone

Le fasi sono **dipendenze funzionali**, non date promesse. M1 è il primo obiettivo user-facing, ma è subordinato ai gate M0. M2–M5 possono avere ricerche/spike paralleli senza attivare funzionalità per utenti reali.

## Provider parity (Azure SaaS ↔ Docker OSS)

Le feature #19 (contract DI), #33 (vault/rotation), #62 (object storage), #63 (queue) e #64 (AI) coprono anche la necessità di poter provare una soluzione tutta OSS: PostgreSQL/pgvector, Keycloak, MinIO, RabbitMQ, Ollama/Whisper, OpenBao (secrets) e, solo quando serve, Valkey (cache) e SignalR (feedback realtime). Questi sono **target**; la disponibilità corrente è descritta in README/architecture e non deve essere confusa con un ambiente completo. Nessun adapter deve introdurre una dipendenza Azure nel layer Domain/Application.

**Decisione bloccante esterna:** #131 richiede una scelta esplicita del proprietario su licensing e policy legali prima del lancio. Nessuna licenza è stata selezionata automaticamente.

## Delta verificato — #137 (18 novembre 2026)

Prima base crittografica per #33, con `IEnvelopeEncryption` e AEAD AES-256-GCM/DEK-KEK, load key ring esterno e rewrap per key rotation. Questo è un **componente di libreria**, non significa che le colonne PostgreSQL o i token esistenti siano già cifrati. Non chiudere parent #33 sino a migrazione+backfill cifrati, vault OSS/Azure, external storage e restore test. Il conteggio delle issue nell'intestazione è storico: per lo stato corrente usare sempre l'API GitHub.


## Aggiornamento operativo #139 (2026-11-18)

I nuovi body delle note vengono scritti come `sealed_payload` nel PostgreSQL primario solo quando la key ring esterna è caricata, mentre un trigger vieta il ritorno alle scritture plaintext. `#138` e `#33` **rimangono aperte**: backfill legacy, cifratura embedding/metadati, Key Vault/OpenBao, backup e verifica GDPR sono ancora da fare. Stato definitivo da evidenze GitHub Actions, non dalla sola presenza del codice.

## Status note 2026-11-21 — Azure WORM immutable version PIN (ADR 0053)

Le 14 epic e le 116 feature originali restano snapshot iniziale, **non**
contatori aggiornati GitHub. La nuova [child #154](https://github.com/KeyserDSoze/Omnia/issues/154),
parent [#144](https://github.com/KeyserDSoze/Omnia/issues/144),
sviluppa il PIN HMAC firmato per esatte versioni Azure WORM senza
pretendere reale custodia offsite, monotonicità o esito della cancellazione.
Per stato attuale e test consultare le issue/Actions GitHub, non il
conteggio storico in intestazione.

## DR Azure note — PIN version-history audit (#155, ADR 0054)

La nuova child [#155](https://github.com/KeyserDSoze/Omnia/issues/155)
segue la #154 senza duplicare l'audit dell'evidence #152: esamina
soltanto la cronologia **visibile del PIN firmato** via Azure SDK
in sola lettura. NON completa l'epic #144 o le issue #141/#109/#31.
I numeri originali 14 epics/116 child nel backlog sono uno snapshot
del 17 novembre: verificare i contatori reali su GitHub.

## HTTP JSON anti-abuse slice (#156, ADR 0055)

La child [#156](https://github.com/KeyserDSoze/Omnia/issues/156)
aggiorna la parent [#37](https://github.com/KeyserDSoze/Omnia/issues/37):
413/415 fail-closed per JSON POST/PUT/PATCH `/api/v1`, incluso
chunked e Content-Length mancante, senza perdere il body valido.
Parent #37 ancora aperta per quote distribuite, media, connector,
slow-body timeout, testing di carico e WAF. Contatori issue nel
backlog storico non rappresentano lo stato corrente di GitHub.

## P0 API body read timeout (#157, ADR 0056)

La child [#157](https://github.com/KeyserDSoze/Omnia/issues/157)
segue #156: introdotto timeout **totale** per lettura JSON con 408
senza side effects, conservando 413/415, OIDC e 429. La [parent #37](https://github.com/KeyserDSoze/Omnia/issues/37)
resta OPEN per slow headers/real ingress, rate-limit distribuito,
WAF, quote media/connector e k6. Lo snapshot del backlog del
17 novembre non è il conteggio live delle issue GitHub.

## P0 API bounded body read concurrency (#159, ADR 0058)

Child [#159](https://github.com/KeyserDSoze/Omnia/issues/159)
extends [#37](https://github.com/KeyserDSoze/Omnia/issues/37)
after #156/#157/#158: per-instance, nonqueueing cap on **in-flight
JSON request-body reads** with fail-fast 503, generic no-store
response and permit release before handler work. This is not a
multi-replica budget, connection limiter, fair tenant quota, WAF
or production ingress test. The historical 14-epic/116-child
inventory is not a current live issue count; consult GitHub.

## P0 Kestrel pre-auth header budget (#160, ADR 0059)

New child [#160](https://github.com/KeyserDSoze/Omnia/issues/160)
extends [#37](https://github.com/KeyserDSoze/Omnia/issues/37)
after #156–#159: server-level Kestrel short request-header timeout,
bounded header bytes and count, default minimum body data rate,
and loopback TCP incomplete-header contract test. This does not
harden upstream TLS, proxies, Azure ingress, global quotas, media,
HTTP/3 or WAF. Original backlog counts are historical, not live.

## P0 active-only per-actor JSON receiver fairness (#161 / ADR 0060)

Child [#161](https://github.com/KeyserDSoze/Omnia/issues/161)
follows the global per-instance concurrent-read cap [#159](https://github.com/KeyserDSoze/Omnia/issues/159).
It adds atomic per-OIDC tenant+subject read admission (default 4
alongside default 32 global) and immediate eviction of idle actor
partition counts, while preserving 503/no-store, 408/413/415 and
release-before-handler invariants. It does not add distributed,
cross-tenant or multi-account fairness, provider quotas or WAF;
[father #37](https://github.com/KeyserDSoze/Omnia/issues/37)
remains OPEN. Original backlog issue counts are historical.

## P0 process-local per-tenant JSON body receive admission (#162, ADR 0061)

Child [#162](https://github.com/KeyserDSoze/Omnia/issues/162)
extends #159 and #161 under [#37](https://github.com/KeyserDSoze/Omnia/issues/37):
three atomic budgets for globally active JSON reads, one validated
OIDC tenant and one validated tenant+subject actor. Default process
caps 32/16/4 (global/tenant/actor); reject 503 without body read,
release before handler and remove idle actor/tenant partitions.
Not a reserved-fair tenant scheduler or distributed multi-replica
quota, nor a media/upload/WAF/GDPR completion. The original issue
counts in this historical backlog are NOT current live counts.

## P0 bounded JSON buffer lifetime across slow handlers (#163 / ADR 0062)

Child https://github.com/KeyserDSoze/Omnia/issues/163 under #37
adds independent process-local max concurrent buffered JSON
requests (default 64, valid 1..256), acquired before read, held
during downstream handler, released after completed processing
or any error, without changing 32/16/4 JSON read quotas.
HTTP 503 request_buffer_busy before any body read on saturation.
This does not bound total process memory, all DB/AI concurrency,
cross-replica quotas, tenant fairness, WAF/ingress or binary/media
uploads. Parent #37 and privacy epic #2 remain OPEN.

## P0 per-tenant bound for retained JSON handler buffers (#164, ADR 0063)

Child [#164](https://github.com/KeyserDSoze/Omnia/issues/164)
extends [#163](https://github.com/KeyserDSoze/Omnia/issues/163)
under [#37](https://github.com/KeyserDSoze/Omnia/issues/37):
additional per-process/verified-tenant admission for completed
JSON buffers retained during slow handlers. Default 64 global,
16 per tenant (or auto-clamped to a lower global), nonqueue
atomic limits, 503 no-read on tenant saturation, release after
handler and eviction of idle tenant keys. Does not change
32/16/4 receive-only permits. No per-actor handler fairness,
distributed service, guaranteed reserved tenant capacity,
connector/media/AI quotas, WAF, realistic load or GDPR go-live.
Original backlog counts are historical, not current GitHub issue
counts.

## P0 actor-scoped retained JSON buffer fairness (#165 / ADR 0064)

Child [#165](https://github.com/KeyserDSoze/Omnia/issues/165)
extends the bounded handler-buffer lifetime work #163/#164 under
parent [#37](https://github.com/KeyserDSoze/Omnia/issues/37):
global 64, signed tenant 16, and signed tenant+subject actor 4
default process-local simultaneous JSON buffers (auto-clamped
when configured capacities are lower). Atomic nonqueued
admission, generic 503 without body read, release only after
downstream handler, and immediate eviction of idle tenant/
actor quota keys. The body RECEIVER 32/16/4 limits remain
separate and end before handler processing. No guaranteed
tenant reservations, cluster-wide quotas, WAF/ingress,
media/connector/storage budgets, realistic load or GDPR
go-live claims; #37 and #2 stay OPEN. Historical inventory
counts above do not reflect live issue totals.

## P0 aggregate signed tenant requests/minute (#166 / ADR 0065)
Child [#166](https://github.com/KeyserDSoze/Omnia/issues/166)
under [#37](https://github.com/KeyserDSoze/Omnia/issues/37):
60-second fixed window, default 600 accepted authenticated
/api/v1 requests per signed OIDC tenant per API process,
alongside existing per-account 120/min. No queue; 429
rate_limited before body/handler, no client-header trust.
Limit tracked tenant partitions to 4096 by default (max
16384), lazily prune expired keys on traffic; fail closed
for new tenants when the map is full. Not a distributed
quota, ingress/WAF, media/connector/provider/storage
budget, fair-share promise or GDPR release. Original
backlog issue totals remain historical. #37/#2 OPEN.

## P0 bounded actor rate partition cardinality (#167 / ADR 0066)

Child [#167](https://github.com/KeyserDSoze/Omnia/issues/167)
under [#37](https://github.com/KeyserDSoze/Omnia/issues/37)
removes the unbounded per-account ASP.NET partition
store, replacing it with shared-engine process-local
signed tenant+subject fixed 60s windows capped at
**8192 actors**, valid configurable 1..32768. Existing
per actor 120/min, per tenant 600/min and tenant
cardinality 4096 remain. At full actor cardinality,
new actors get 429/no-store before any body reads
or handler; already tracked actors can use remaining
budget until expiry. Lazy 15s traffic-triggered
cleanup and no O(N) per-denial scans. This is not a
distributed quota or reserved-fair ingress, WAF,
media/storage/connector/AI budget, load-test result
or GDPR go-live; parent #37 and #2 OPEN. Historical
issue counts above are not current totals.


## #183 — Web day grouping over already-loaded timeline rows

Child of #79 / UX epic #7, following #182 source-filter slice.
An Italian accessible header groups canonical signed
`occurredAt` values in the DEVICE timezone, including
midnight/DST and later keyset pages; source filtering is
applied before grouping. It is client-only, not a server
day query or automatic collection. Authentication, RLS,
retention and erasure behavior are unchanged. See ADR 0081
and child #183 for RED→GREEN real Keycloak/CI evidence.
#79/#41/#7/#2 remain OPEN for broader requirements.


## #184 — first-party Web loaded-tag filter

Child of #79/#7 after #182 source filter and #183 local
day headings. The signed-memory Web view exposes exact
keyword-tag selection independently of source choice,
then intersects both selections before grouping days,
without fetching unbounded pages or changing the DB.
The server's `tags` are heuristic metadata, not LLM
insights or cross-provider permissions. Exact tests/CI and
limitations are in issue #184 and ADR 0082. #79/#41/#2
remain OPEN; no global query, tag editing or native sync.
## #185 — selezione giorno locale su timeline Web già caricata

Child #79/#7 dopo #182/#183/#184: il giorno è una selezione locale delle sole righe JWT/RLS già caricate; source/tag/day AND, selettore accessibile, paging indipendente e cancellazione della selezione a fine sessione. Non sostituisce server-side query, thread, data originale del provider, consenso esterno o ricerca su tutti i dati. ADR 0083; documentazione della prova TDD in #185. Issue parent OPEN.
