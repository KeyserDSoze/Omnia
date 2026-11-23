# Ricerca testuale locale della timeline Web (#187)

L'utente autenticato cerca una parte del contenuto visualizzato soltanto
tra le note effettivamente caricate con GET /api/v1/memories/page sotto JWT
e PostgreSQL FORCE RLS. La funzione non cerca nelle altre pagine, nel raw,
negli allegati, nei servizi esterni né nella memoria semantica.
L'input è massimo 100 caratteri; ignora maiuscole, accenti latini e spazi
ripetuti.

Il filtro viene combinato con sorgente, tag e giornata locale usando AND:
l'ordine temporale, il cursore e i titoli delle giornate restano invariati.
Uno stato vuoto non prova che la nota non esista in pagine non richieste.
Il pulsante Carica altre note resta disponibile quando il cursore lo
consente e i risultati possono aumentare dopo un caricamento esplicito.

La ricerca è effimera e non salva query o testo in URL, browser storage,
log o analytics. Logout, API 401 e cancellazione PostgreSQL principale
confermata eliminano la query con le note. Non implementa ricerca
full-account, native/offline, connettori, thread né cancellazione dei backup.
Restano aperte #79/#7/#41/#17/#2.

ADR: ../adr/0084-loaded-web-timeline-text-filter.md
Tracking: https://github.com/KeyserDSoze/Omnia/issues/187
