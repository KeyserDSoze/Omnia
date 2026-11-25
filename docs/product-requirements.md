# Tao — Product Requirements (v0.4)

## Tesi

Tao è l'assistente personale di lavoro che **conosce la tua giornata** perché è collegato ai tuoi
strumenti, e ti aiuta a deciderla in pochi secondi. Gira sul tuo server: i dati restano tuoi.

## Persona

Professionista con lavoro distribuito tra Microsoft 365 e/o Google Workspace, CRM, Teams e molte
riunioni; poco tempo, molte notifiche, vuole sapere ogni mattina cosa conta e chiudere i cicli
aperti anche in movimento (es. mentre corre con le cuffie).

## Business case (prima release e successive)

1. **Connesso**: Microsoft Graph e Google, poi CRM e transcript. *(R1 in parte, R2)*
2. **Lista giornaliera da smistare** stile "Tinder": fatto / non fatto / dopo (in coda) / scarta,
   con pre-azioni e azioni. *(R1)*
3. **Azioni sulle app** solo con conferma esplicita e audit. *(R1: completare task)*
4. **Piano del giorno** con focus e pre-azioni suggerite dall'AI, opt-in. *(R1)*
5. **Uso vocale** in cuffia. *(R2)*
6. **Ping** e promemoria. *(R2)*
7. **Memoria giornaliera** costruita da CRM, Teams, transcript, chat e mail. *(R2)*
8. **Logiche di business** spiegate dall'utente, brainstorming. *(R2)*
9. **Stile comunicativo**: Tao si adatta al tuo modo di comunicare (profilo opt-in, modificabile,
   mai diagnosi psicologiche). *(R2)*
10. **Dashboard web e client desktop** Mac/Windows. *(R3)*

## Journey R1

1. L'operatore installa Tao (`docker/setup.sh`, compose, tunnel) e crea l'utente in Keycloak;
   se vuole abilita l'accesso con Google/Microsoft (`TAO_SOCIAL_LOGIN`).
2. L'utente accede e trova una chat. Il bottone ✨ *Configura Tao* fa domande fisse, senza AI:
   account Microsoft e Google (lavoro o personale), Outlook, Teams, To Do, Gmail, Calendar, Tasks,
   altri account, CRM, orari, modalità dell'agente (sola lettura o azioni con conferma).
   Le risposte diventano impostazioni e deleghe salvate su PostgreSQL. Poi Tao propone di collegare
   ogni account (popup OAuth). Le deleghe si cambiano in ogni momento dalle impostazioni in alto a destra.
3. Al mattino la chat saluta con gli spunti dei giorni prima; «cosa devo fare oggi?» apre il deck:
   una carta alla volta, smistata con swipe o pulsanti; per le attività, se delegato, può spuntare
   "segna completata anche su To Do/Tasks".
4. Se vuole, attiva l'assistente (consenso dedicato) e genera il piano del giorno: le pre-azioni
   compaiono sulle carte.
5. Può esportare o cancellare tutto dalla vista *Note e privacy*.

## Requisiti non funzionali

- Privacy by design: consenso per scopo, minimizzazione, FORCE RLS, sigillatura, export/cancellazione.
- Nessuna azione automatica verso terzi; nessuna registrazione non consensuale.
- Funziona con un solo host e una sola porta; installazione in meno di 15 minuti.
- Interfaccia in italiano, tema chiaro/scuro, utilizzabile da tastiera e touch.

## Metriche (uso personale)

- Tempo per smistare la giornata (obiettivo < 3 minuti).
- Carte chiuse "fatto" per giorno; carte rimandate più volte (segnale di attrito).
- Errori di connessione per account (deve tendere a zero).

## Fuori scope R1

SaaS multi-tenant con billing, app store, WhatsApp personale, registrazione di chiamate,
automazioni senza conferma.
