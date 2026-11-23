# Filtro per giorno della timeline Web (#185)

Le note già caricate da `GET /api/v1/memories/page` sotto JWT valido e PostgreSQL FORCE RLS contengono un `occurredAt` canonico. Il nuovo selettore propone **Tutti i giorni** e un'opzione per ogni giornata **effettivamente presente nelle pagine già caricate**, con data italiana calcolata nel fuso locale di questo dispositivo.

Il giorno selezionato si applica in **AND** ai filtri sorgente (#182) e tag (#184). La timeline continua a raggruppare le righe corrispondenti per giorno (#183). Le opzioni di selezione vengono derivate da tutte le righe caricate, indipendentemente dai filtri attivi: in caso di zero risultati puoi cambiare un filtro oppure usare **Carica altre note**. Non vengono effettuate query aggiuntive né ricerche automatiche su tutte le date del database.

Le etichette tag esistenti restano euristiche di parole chiave, non insight AI. Le date rappresentano il giorno nel fuso del dispositivo, non necessariamente la data che aveva il sistema esterno al momento dell'evento. Le altre pagine possono contenere giornate non ancora proposte. Il filtro non crea thread, non aggiorna dati o consensi e non conserva dati in URL o browser storage. Logout, 401 e cancellazione dell'account sul database principale eliminano selezioni e note insieme.

Dettagli architetturali: [ADR 0083](../adr/0083-loaded-web-timeline-local-day-selection.md); prove TDD e test Keycloak: [issue #185](https://github.com/KeyserDSoze/Omnia/issues/185). Restano aperte #79, #7, #41, #17, #2 per funzioni generali e privacy non completata.
