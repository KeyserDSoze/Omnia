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
- Senza carte aperte il modello non viene chiamato e il riepilogo è vuoto: la lingua dell'utente
  la dice il client.
- Il risultato non viene salvato: resta nella pagina finché non ricarichi.

## Lingua

- Il piano è scritto nella lingua scelta dall'utente nell'app (21 lingue, [ADR 0103](../adr/0103-internationalization-21-languages.md)).
- Il client invia `language` con un codice (`it`, `en`, `ar`, ...). Il server accetta solo codici
  di un elenco fisso (`AssistantLanguages`) e al prompt passa solo il nome inglese preso da lì:
  il testo libero non arriva mai al prompt. Senza codice: italiano.
- Le date sono formattate con la cultura della lingua ma sempre nel calendario gregoriano.

## Sicurezza

- Il contenuto delle carte (email, chat) è **input non affidabile**: il prompt lo dichiara e
  l'output è vincolato a un JSON con riferimenti alle sole carte inviate; suggerimenti con
  riferimenti sconosciuti vengono scartati.
- L'output è etichettato "AI", è solo testo e non esegue mai azioni.
- Errori del modello: codici `model_error`, `model_unreachable`, `model_rate_limited`,
  `invalid_model_output` (502) senza il corpo della risposta del provider.

## API

| Metodo | Percorso | Note |
|---|---|---|
| GET | `/api/v1/assistant` | `{ configured, model, host }` |
| POST | `/api/v1/today/briefing` | `{ from?, to?, timeZone?, language? }` (IANA, es. `Europe/Rome`; `language` da elenco, es. `en`) → `{ summary, suggestions[{key, preAction}], model, generatedAt, cardsSent }`; 403 `assistant_consent_required`, 409 `assistant_not_configured`, 400 `invalid_time_zone`, 400 `invalid_language` |
