# Timeline giornaliera locale Web — #183 / ADR 0081

La timeline Web autenticata usa il campo canonico `occurredAt`
ritornato dalla API PostgreSQL firmata OIDC/JWT e protetta da
Row-Level Security. Con `Intl.DateTimeFormat` presenta una
intestazione italiana per ogni **data del calendario nel fuso
locale del dispositivo**, seguita dalle note con ora, sorgente
e tag. La conversione avviene solo in visualizzazione: non si
riscrive la provenienza o il timestamp dell'evento.

- **Fuso locale:** mezzanotte e DST sono calcolati con la
  timezone del dispositivo (non con taglio dei primi dieci
  caratteri della data UTC). Fusi diversi possono mostrare
  lo stesso evento in due giornate locali differenti.
- **Paging:** la API consegna 10 note per pagina in ordine
  deterministico; un gruppo che continua nella pagina
  successiva si estende senza seconda intestazione. L'utente
  carica esplicitamente le pagine con Carica altre note.
- **Fonte:** il filtro #182 seleziona solo le righe già caricate,
  poi i gruppi si derivano dal risultato visibile. Quando non
  ci sono risultati il pulsante delle pagine continua a
  funzionare. Questo non è ricerca globale.
- **Privacy:** le intestazioni non sono uno store di dati
  indipendente. Logout, 401 ed erasure svuotano le note,
  quindi anche i gruppi; l'epoch Web impedisce che una
  risposta precedente ripopoli i dati di un altro account.

La data rappresenta l'ora dell'evento fornita dall'API e
non la data di acquisizione o di scadenza retention; nessuna
intelligenza o classificazione temporale è stata dedotta.
Il backend non salva ancora la timezone originale di ogni
sorgente. Gli scenari TDD Vitest e browser Keycloak reali
sono nella [issue #183](https://github.com/KeyserDSoze/Tao/issues/183).
La #79 resta aperta per thread, tag, persone e parità native.


## Filtraggio tag (#184)

La UI applica il filtro sorgente (#182) e quello tag
(#184) **prima** di derivare i giorni locali. Una giornata
senza note visibili non mostra intestazioni residue;
una pagina successiva può aggiungere altre note o tag.
[Guida ai filtri](timeline-tag-filter.md) · ADR 0082.
## Selezione di un solo giorno già caricato (#185)

Il pulsante **Tutti i giorni** e le date locali visibili derivano dalle stesse righe già scaricate usate dal grouping. Il giorno scelto viene combinato con i filtri sorgente/tag prima di renderizzare le intestazioni; il tasto **Carica altre note** resta indipendente. Vedi [guida al filtro giornata](timeline-day-filter.md) e ADR 0083.
