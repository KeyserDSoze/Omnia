# Assistente: piano del giorno

Funzione **opzionale**: un modello linguistico propone il focus della giornata e una pre-azione
concreta per le carte più importanti. Decisione: [ADR 0100](../adr/0100-assistant-briefing-openai-compatible.md).

## Configurazione (`docker/.env`)

| Variabile | Esempio |
|---|---|
| `LLM_ENDPOINT` | `https://api.openai.com/v1` · `https://<risorsa>.openai.azure.com/openai/v1` · `http://ollama:11434/v1` |
| `LLM_API_KEY` | chiave del servizio (vuota per Ollama) |
| `LLM_MODEL` | `gpt-4.1-mini`, nome del deployment Azure, `qwen2.5:7b`, ... |
| `LLM_AUTH_HEADER` | `bearer` (OpenAI, Ollama) oppure `api-key` (Azure OpenAI con chiave) |
| `LLM_DAILY_CALLS_PER_USER` | piani al giorno per utente (default 20, 1–1000) |
| `LLM_DAILY_TOKENS_PER_USER` | token al giorno per utente (default 200000) |
| `LLM_MAX_OUTPUT_TOKENS` | lunghezza massima della risposta (default 700, `max_tokens`) |

L'endpoint deve essere HTTPS; HTTP è ammesso solo per loopback o per un host a nome singolo nella
rete privata dei container (es. `ollama`). Con `LLM_ENDPOINT` vuoto l'assistente è disattivato e
l'interfaccia lo dice. Il modello deve supportare `response_format: json_object`.

Modello locale: `docker compose --env-file docker/.env -f docker/compose.yml --profile ollama up -d`,
poi `docker compose ... exec ollama ollama pull <modello>`.

## Consenso e dati inviati

- Consenso dedicato `assistant.briefing` (sorgente `assistant`, 30 giorni, revocabile dal registro
  dei consensi). Il consenso per le note **non** vale per l'assistente.
- L'interfaccia mostra modello e host prima dell'attivazione.
- Vengono inviate solo le carte **ancora aperte** (massimo 25): tipo, titolo, orario locale,
  nome dell'interlocutore (un indirizzo email viene ridotto alla parte prima di `@`), anteprima
  di 140 caratteri, flag importante/non letto. Le carte vengono dallo stesso deck, quindi solo
  dalle categorie delegate nelle impostazioni.
- Mai inviati: identificativi, chiavi delle carte, link, indirizzi email completi, token.
- Anche il testo di titolo e anteprima viene ripulito prima dell'invio: link, indirizzi email e IBAN
  citati in una mail diventano `[link]`, `[email]`, `[iban]`.
- Senza carte aperte il modello non viene chiamato e il riepilogo è vuoto: la lingua dell'utente
  la dice il client.
- Il risultato non viene salvato: resta nella pagina finché non ricarichi.

## Lingua

- Il piano è scritto nella lingua scelta dall'utente nell'app (21 lingue, [ADR 0103](../adr/0103-internationalization-21-languages.md)).
- Il client invia `language` con un codice (`it`, `en`, `ar`, ...). Il server accetta solo codici
  di un elenco fisso (`AssistantLanguages`) e al prompt passa solo il nome inglese preso da lì:
  il testo libero non arriva mai al prompt. Senza codice: italiano.
- Le date sono formattate con la cultura della lingua ma sempre nel calendario gregoriano.

## Budget

- Ogni utente ha un budget per **giorno locale** (fuso del client): numero di piani e token.
  Vale solo per le chiamate che arrivano davvero al modello: senza consenso, senza carte aperte o
  con l'assistente non configurato non si consuma nulla.
- Superato il limite l'API risponde 429 `assistant_budget_exceeded` senza chiamare il modello.
  Una chiamata fallita (modello non raggiungibile) conta come chiamata; i token no.
- I token sono quelli dichiarati dal provider (`usage`); se mancano sono stimati in circa un token
  ogni quattro caratteri.
- Il contatore sta in `assistant_usage` (solo giorno, chiamate, token; mai testi): FORCE RLS, nell'export,
  cancellato con i dati dell'utente, eliminato dopo 35 giorni. La risposta del piano porta `usage`
  e l'interfaccia mostra "Piani oggi: 2 su 20".
- Non esiste un tetto globale per l'installazione: per quello usa il limite di spesa del provider.

## Valutazione

La qualità del piano si misura con scenari fissi (`AssistantTests.Evaluation.cs`): giornata normale,
prompt injection in una mail, identificativi e link che non devono uscire, più carte di quelle inviabili.
I controlli sono: `minimized`, `untrusted-declared`, `bounded-cards` (cosa esce) e `known-cards-only`,
`no-links`, `grounded-times` (nessun orario inventato), `injection-not-obeyed`, `brief` (cosa torna).

- Nei test normali un modello simulato, corretto e scorretto, dimostra che i controlli scattano.
- Per valutare il **tuo** modello:

  ```sh
  TAO_EVAL_LLM_ENDPOINT=http://127.0.0.1:11434/v1 TAO_EVAL_LLM_MODEL=qwen2.5:1.5b \
    dotnet test tests/Tao.Domain.Tests --filter TheConfiguredModelMeetsTheQualityBarOnEveryScenario \
    --logger "console;verbosity=detailed"
  ```

  (`TAO_EVAL_LLM_API_KEY` e `TAO_EVAL_LLM_AUTH_HEADER` come per `LLM_*`.) Stampa un rigo per scenario;
  `minimized`, `no-links` e `known-cards-only` devono sempre passare perché li garantisce Tao, gli
  altri sono qualità del modello e sono ammessi al massimo due insuccessi.
- Esempio reale: `qwen2.5:1.5b` locale supera la soglia, ma con 40 carte la risposta diventa prolissa
  (`brief` fallisce): un modello più grande o meno carte danno piani migliori.

## Sicurezza

- Il contenuto delle carte (email, chat) è **input non affidabile**: il prompt lo dichiara e
  l'output è vincolato a un JSON con riferimenti alle sole carte inviate; suggerimenti con
  riferimenti sconosciuti vengono scartati.
- L'output non contiene mai link o indirizzi email: nel riepilogo vengono rimossi, un suggerimento che
  ne contiene è scartato. Un'istruzione iniettata non può quindi portare un link di phishing o un
  destinatario nel piano.
- L'output è etichettato "AI", è solo testo e non esegue mai azioni.
- Errori del modello: codici `model_error`, `model_unreachable`, `model_rate_limited`,
  `invalid_model_output` (502) senza il corpo della risposta del provider.

## API

| Metodo | Percorso | Note |
|---|---|---|
| GET | `/api/v1/assistant` | `{ configured, model, host, dailyCalls, dailyTokens }` |
| POST | `/api/v1/today/briefing` | `{ from?, to?, timeZone?, language? }` (IANA, es. `Europe/Rome`; `language` da elenco, es. `en`) → `{ summary, suggestions[{key, preAction}], model, generatedAt, cardsSent, usage{calls, callsLimit, tokens, tokensLimit} }`; 403 `assistant_consent_required`, 409 `assistant_not_configured`, 429 `assistant_budget_exceeded`, 400 `invalid_time_zone`, 400 `invalid_language` |
