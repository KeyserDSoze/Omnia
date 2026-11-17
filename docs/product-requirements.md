# Omnia — Product Requirements Document v0.1

**Product thesis:** Omnia è una *personal intelligence layer* data-owned: registra, integra e contestualizza **solo** informazioni autorizzate; rende la memoria ricercabile, mostra insight con fonti e permette di delegare azioni revocabili. Non è un sistema per sorvegliare terzi o impersonarli senza consenso.

## Persona e jobs-to-be-done

- Professionista: dopo una riunione voglio salvare eventi, decisioni e persone collegate senza ricopiare in dieci app.
- Persona con vita digitale frammentata: voglio vedere un riepilogo coerente di email, calendario, CRM e note che ho autorizzato.
- Utente avanzato: desidero connettere MCP e strumenti IoT con scope limitato, monitorandone ciascuna capacità.
- Utente privato: voglio suggerimenti e riflessioni contestuali, **non** diagnosi né giudizi assoluti su di me.

## Promessa MVP M1

Con un primo login sicuro e due device, l'utente salva una nota in un tap, vede stato di sync reale, la trova nella timeline e può cancellarla/esportarla. **Il backend non vive su GitHub Pages:** il frontend statico Expo ha un API HTTPS Azure separato. L'interfaccia non deve chiedere tutti i permessi in fase di onboarding.

## User journeys principali

1. **Onboard:** apri Pages/app → login OIDC PKCE → account personale e privacy choices → nota demo sintetica → timeline.
2. **Capture:** tasto uno-tap → testo o memo microfono consentito → sorgente, timestamp, contentType, hash → uploaded/pending/failed → normalizzato/taggato → indicizzato.
3. **Connect:** catalogo/provider ufficiale → visualizza accesso richiesto, duration/scopes → OAuth → import incremental → thread e provenance → revoca/cancellazione fonte.
4. **Understand:** scegli giorno/persona/topic → summary con citazioni ai source events → insight opt-in non diagnostico → utente corregge e Omnia versiona la correzione.
5. **Act:** Omnia propone bozza/azione → mostra destinatario, scope, effetti → edit/approve → execute once → receipt e audit → stop/revoke.
6. **Exit:** export dei dati + correzioni + derivati; cancellazione con tracking stato in storage, indici e provider downstream e retention legale documentata.

## Canonical memory contract (target, non interamente implementato)

```text
EventEnvelope
  id, schemaVersion, tenantId, subjectId, sourceId, sourceItemId, connectorVersion
  occurredAt, observedAt, receivedAt, clockConfidence, timeZone
  contentType, rawStorageRef | rawText, rawSha256, language, participants (consented)
  normalizedJson, summary, userTags[], suggestedTags[], inferenceConfidence
  embeddings[] {model, dimension, vectorRef, status}, threadIds[], entityRefs[]
  consentGrantId, visibility, provenance[], deleteState, transformationVersions[]
```

Il raw rimane immutabile: trasformazioni sono append-only con version e trace, salvo cancellazione autorizzata. Model output separato da evidenze, tag personalizzabili, embedding nullable (niente finti vettori), supporto di vector reindex dopo cambio modello. Filtri tenant/subject/server enforced e RLS come difesa in profondità. Eventi duplicati e fuori ordine non moltiplicano azioni o messaggi.

## Platform and provider matrix

| Capacità | Azure SaaS target | Docker OSS target | Implementazione attuale |
|---|---|---|---|
| DB e vector | PostgreSQL Flexible Server + pgvector | PostgreSQL pgvector | baseline reale; cloud non deployato |
| API/app | Container Apps .NET 10 + ACR; Expo Pages/native | ASP.NET + Expo | API shell + Web statico |
| Login | Entra External ID | Keycloak | solo JWT server, UI PKCE mancante |
| Storage binary | Blob | MinIO | port non implementato |
| Queue | Service Bus | RabbitMQ | port non implementato |
| LLM/embeddings/STT | Azure OpenAI/Azure Speech | Ollama/Whisper | solo embedding adapter, STT no |
| Telemetry | Azure Monitor/Application Insights | OpenTelemetry/Jaeger | non implementato |
| Secrets | Key Vault/Managed Identity | dev secrets (no production parity) | target |
| OAuth connectors | Graph, CRM e API provider ufficiali | mock contract/server locale | non implementato |
| MCP | remote authorized MCP gateway | local sandbox server | non implementato |

## Nonfunctional quality targets (obiettivi, non SLA correnti)

- UI: primo capture facile da trovare, tema light/dark, italiano/inglese, WCAG 2.2 AA target, VoiceOver/TalkBack e reduced motion.
- Reliability: API low-latency anche se AI non disponibile; processi AI asincroni con retry/DLQ; sync idempotente. Definire SLO misurabili dopo baseline.
- Security: privacy-by-default, no secret in bundle/GitHub Pages, explicit data use/source grant, rotating keys, auditing, retention, export/erase.
- Performance: E2E p95 e costi definiti solo dopo benchmark per tipo di load; bounded contexts scalabili prima di introdurre microservizi.
- Portability: OpenAPI, export standardizzato, no vendor logic nel domain, contract parity per adapter; PostgreSQL comune cloud/local.
- AI: un insight deve mostrare provenienza e poter essere contestato; distinguere recapitolo da valutazione soggettiva.
- Native: build Android/iOS validate su device; registrazioni chiamate PSTN solo se piattaforma/provider/consenso lo consentono, altrimenti import autorizzato.

## Cosa NON promettiamo

Accesso illimitato alle chat personali WhatsApp, intercettazione audio di qualsiasi chiamata iOS/Android, registrazioni invisibili, clone vocale senza consenso, pubblicazione e messaggistica autonome prive di autorizzazione, profili psicologici/clinici, deploy Azure pronto senza hardening e compliance.

## Roadmap e feature

Fonte di verità per pianificazione: [backlog](backlog.md) e [GitHub Issues](https://github.com/KeyserDSoze/Omnia/issues). La validazione business richiede review di utenti e consulenza GDPR; il PRD è una specifica di progetto, non evidenza che ogni servizio esista.
