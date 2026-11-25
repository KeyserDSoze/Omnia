# Roadmap

Il backlog operativo è su GitHub (epic #221–#230); qui la sequenza e il perché.

## R1 — Prima release on-prem (in corso)

Obiettivo: usare Tao ogni mattina sul proprio server per smistare la giornata di lavoro.

| Epic | Stato |
|---|---|
| #221 Installazione on-prem: gateway, Keycloak, Cloudflare Tunnel, backup | gateway, setup e accesso Google/Microsoft su invito fatti; mancano runbook backup/restore e aggiornamento |
| #222 Connettori Microsoft 365 e Google Workspace | lettura, completamento task, account multipli e deleghe per account fatti; delta/webhook, contatti, CRM da fare |
| #223 Deck "Oggi" | fatto, dentro la chat iniziale con configurazione guidata; undo, scorciatoie, coda per domani da fare |
| #224 Piano del giorno (AI) | prima versione fatta; valutazione e budget da fare |

Criterio di uscita: una settimana d'uso reale con almeno un account collegato, backup provato,
nessun dato personale nei log, test verdi in CI.

## R2 — Assistente sempre con te

| Epic | Idea |
|---|---|
| #225 Voce in cuffia | ascoltare le carte e smistarle a voce mentre corri, push-to-talk |
| #226 Ping e notifiche | promemoria e carte urgenti con quiet hours |
| #227 Memoria giornaliera | diario da mail, chat, Teams, transcript, CRM con consenso e retention |
| #228 Flussi di business | logiche spiegate dall'utente che generano pre-azioni e azioni confermate |
| #229 Stile comunicativo | profilo opt-in, modificabile, per bozze nel tuo tono (mai diagnosi) |

## R3 — Ovunque

| Epic | Idea |
|---|---|
| #230 Desktop e mobile | dashboard completa, client Mac/Windows, app iOS/Android |

## Principi che non cambiano

- Lettura prima della scrittura; ogni azione su un'app esterna è confermata e registrata.
- Consenso per scopo, minimizzazione, export e cancellazione per ogni nuovo dato.
- L'AI propone, l'utente decide; l'output AI è sempre etichettato.
- Test reali (PostgreSQL, browser) prima di dichiarare una funzione pronta.
