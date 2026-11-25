# Integrazioni: cosa si può allacciare a Tao

Stato: **progetto** (ADR [0111](../adr/0111-integration-catalog-mcp-api-whatsapp.md)). Oggi sono
disponibili solo Microsoft 365 e Google Workspace ([connettori](connectors.md)). Il resto è pianificato
e il catalogo dirà sempre quale è quale.

Obiettivo di prodotto: allacciare qualunque cosa deve essere **molto, molto facile**, dalla chat o
dalle impostazioni, e la domanda «cosa posso allacciare?» deve avere sempre una risposta chiara.

## La risposta che la chat dovrà dare

1. **Già integrate nella piattaforma**: Microsoft 365 (posta, calendario, attività, chat Teams), Google
   Workspace (Gmail, Calendar, Tasks), WhatsApp (pianificato, due modalità), poi Telegram, Slack e altri canali.
2. **Server MCP** (un indirizzo web che espone strumenti): senza autenticazione, con una chiave in
   un'intestazione, oppure con accesso OAuth (ti do un link per accedere, come in una pagina di login).
3. **API dirette**: bastano un indirizzo, o la descrizione OpenAPI, e una chiave oppure OAuth.

## Come si allaccia (massimo tre passi)

| Passo | Cosa fai | Cosa fa Tao |
|---|---|---|
| 1 | Scegli dall'elenco oppure incolli un indirizzo | Riconosce il tipo: un endpoint MCP risponde a `initialize`; un 401 con `WWW-Authenticate` vuol dire OAuth; un documento OpenAPI vuol dire API diretta |
| 2 | Autorizzi: popup OAuth, chiave incollata, oppure QR (WhatsApp) | Salva le credenziali sigillate sul server, mai nel browser |
| 3 | Scegli cosa può fare (solo lettura di default) | Fa una chiamata di prova e mostra gli strumenti trovati |

## Regole uguali per tutte le integrazioni

- Lettura per default; ogni scrittura chiede la conferma per quella singola azione e lascia una riga di audit.
- Tao chiama solo le capacità che hai delegato.
- Quello che torna da un'integrazione è input non affidabile: limitato in dimensione, mai eseguito, mai inoltrato da solo.
- L'assistente usa gli strumenti di un'integrazione solo con un consenso dedicato.
- Gli indirizzi scritti dall'utente passano da un controllo in uscita (niente reti interne, niente redirect verso altri host).

## WhatsApp

| Modalità | Cos'è | Dove | Rischio |
|---|---|---|---|
| API ufficiale WhatsApp Business su un numero dedicato | **Canale**: scrivi a Tao su WhatsApp; il primo messaggio richiede un codice approvato nell'app, poi elenco dei mittenti ammessi | anche nel servizio ospitato | serve un account Meta business e un numero |
| Dispositivo collegato (tipo Baileys) sul tuo numero | **Sorgente**: Tao legge solo le chat che indichi e la chat con te stesso; non invia mai senza conferma | **solo on-prem**, sperimentale | protocollo non ufficiale: il numero può essere sospeso; i messaggi altrui sono dati personali di terzi, serve una verifica legale |

## Cosa abbiamo studiato di OpenClaw

Sintesi in ADR 0111. In breve: copiamo il *come* (canali come tipo di integrazione, codice di
abbinamento e allowlist, MCP con «aggiungi e prova», link di accesso in chat, filtri sugli strumenti,
catalogo in cache con backoff) e non il *dove gira* (un gateway è un solo confine di fiducia, gli
strumenti hanno accesso all'host, il driver WhatsApp non è ufficiale).

## Backlog

Epic e issue elencati in [backlog](backlog.md). Prima fetta: catalogo + risposta in chat, senza
chiamate in uscita né segreti.
