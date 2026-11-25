# Filtri combinati della timeline Web — tag e sorgente (ADR 0082)

Dalla API autenticata con OIDC/JWT e PostgreSQL FORCE RLS la
timeline Web riceve pagine da 10 note con `source`, `tags`
e `occurredAt`. La UI ora consente di selezionare sia una
**sorgente** (#182), sia un **tag**, mantenendo il gruppo
giornaliero (#183). Le due selezioni sono un'intersezione
`sorgente AND tag`, non una ricerca full-database.

- I tag selezionabili derivano da **tutte le note già caricate**,
  anche se è attivo un filtro sorgente. Le pagine ulteriori
  aggiungono eventualmente nuove opzioni.
- Il match tag è **esatto**, con `Array.includes`:
  non ricerca porzioni di parole o significati semantici.
  Il backend assegna oggi tag tramite semplici parole chiave;
  non sono insight AI né consensi di provider terzi.
- Le note vengono filtrate *prima* del raggruppamento per
  giornata nel fuso locale; nessuna intestazione resta visibile
  per un giorno senza note corrispondenti. L'ora, la sorgente,
  i tag e il timestamp originale sono invariati.
- Il conteggio indica **note visibili rispetto alle note caricate**.
  In caso di zero risultati, la paginazione manuale resta
  disponibile. Non ci sono scansioni automatiche né fetch
  aggiuntivi dovuti ai filtri.
- Logout, 401 e cancellazione sul database primario svuotano
  filtro tag, filtro sorgente, note e gruppi; nessun filtro
  è salvato in URL, browser storage o app native.

Evidenza RED/GREEN e test real Keycloak: [#184](https://github.com/KeyserDSoze/Tao/issues/184).
Decisione: [ADR 0082](../adr/0082-loaded-web-timeline-tag-source-intersection.md).
**Non implementati:** tag modificabili dall'utente, filtri
persona/thread, query globali, sync nativo/offline e
conformità GDPR end-to-end. #79 resta aperta.
## Combinazione con il giorno locale (#185)

Un tag può essere intersecato con fonte e giornata selezionata. Nessuna ricerca di date o etichette su pagine non ancora caricate è implicita. Vedi [guida al giorno](timeline-day-filter.md).
