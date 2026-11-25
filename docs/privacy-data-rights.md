# Privacy e diritti sui dati

Tao tratta dati personali dell'utente e, attraverso email, chat e riunioni, anche dati di terzi.
Principi: consenso per scopo, minimizzazione, conservazione limitata, accesso e cancellazione.

## Cosa conserva Tao

| Dato | Base | Conservazione | Protezione |
|---|---|---|---|
| Note (`memory_entries`) | consenso `memory.capture` per sorgente | 30 giorni | sigillate, FORCE RLS |
| Consensi (`consent_grants`) | registro delle scelte | fino a cancellazione | FORCE RLS |
| Account collegati (`connector_accounts`) | azione esplicita "Collega" | fino a scollegamento o cancellazione | token e identità sigillati |
| Autorizzazioni OAuth in corso | flusso di collegamento | 10 minuti | verifier sigillato, state solo come hash |
| Decisioni sulle carte (`triage_decisions`) | uso del deck | 30 giorni | solo chiave e decisione, niente contenuti |
| Audit azioni (`connector_actions`) | scritture confermate | 180 giorni | azione, esito, ora |
| Impostazioni e deleghe (`user_settings`) | configurazione guidata, compilata dall'utente | fino a cancellazione | documento sigillato, FORCE RLS |

Contenuti di calendario, email, attività e chat **non vengono salvati**: sono letti al momento.
Il piano del giorno AI non viene salvato. La configurazione guidata e la chat sono script fissi:
non inviano nulla a modelli AI e chiedono solo quali account e strumenti usi, non i loro contenuti.
Le deleghe limitano ciò che Tao chiede ai provider: le categorie non delegate non vengono lette.

## Export

`GET /api/v1/privacy/export` (pulsante "Esporta i miei dati") produce un JSON con note (decifrate),
consensi, impostazioni (decifrate), account collegati (identità e scope, **mai i token**),
autorizzazioni in corso, decisioni e audit. Lo stream non viene messo in cache (`no-store`).

## Cancellazione

`POST /api/v1/privacy/erase` con conferma esplicita: l'API revoca prima, quando possibile, i grant
presso i provider (Google), poi cancella in un'unica transazione note, consensi, impostazioni,
account, decisioni, autorizzazioni e audit dell'utente. La ricevuta riporta i conteggi.

Non inclusi: backup dell'operatore, log dell'host, dati presso Microsoft/Google (la posta resta
nella tua casella) e consensi dati alle app presso i provider (per Microsoft rimuovili da
myapps.microsoft.com).

## Terzi

- Le carte mostrano nomi e anteprime di messaggi altrui solo all'utente che le riceve.
- Verso l'AI vanno solo campi minimizzati e con consenso dedicato; gli indirizzi email non vengono inviati.
- Tao non invia messaggi a terzi automaticamente e non registra chiamate.

Per un uso in azienda valutare informativa ai colleghi e DPIA con il proprio DPO.
