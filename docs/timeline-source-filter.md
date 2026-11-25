# Web timeline: source filter over loaded pages (ADR 0080)

Tao's authenticated Web client reads only the actor-scoped
\`GET /api/v1/memories/page?limit=10\` keyset pages. The Web timeline
now offers **Tutte** plus each distinct source **already present in
the loaded rows**, with an exact source filter. Counts explicitly
describe \`visible of loaded\`. An empty filtered view does **not**
mean no matching notes exist elsewhere in the PostgreSQL account:
\`Carica altre note\` still fetches the next signed page; further
source choices become available as those rows arrive.

This filter never sends a new API request, changes the server's
cursor, grants capture permission or deletes a memory. It is a
transient selection, not a tenant/source search key: actor identity
comes exclusively from validated OIDC JWT and FORCE RLS. Logout/401
and erasure clear the filter; stale prior-session responses cannot
repopulate private notes. A full browser reload requires another
login and does not remember the previous selection.

This is a #79/#41 *partial UX* slice, not daily grouping,
semantic/person/tag search, cross-device sync or a GDPR claim.
See [ADR 0080](../adr/0080-web-loaded-timeline-source-filter.md) and
[#182](https://github.com/KeyserDSoze/Tao/issues/182).


## Intestazioni giornaliere (#183)

Dopo il filtro delle righe già caricate, Tao raggruppa le note
visibili in giornate del fuso del dispositivo, ricavate dal campo
`occurredAt` senza modificare il tempo canonico. Le pagine successive
possono estendere lo stesso gruppo senza creare due intestazioni.
Dettagli e limiti: [guida timeline](timeline-day-grouping.md) e
[ADR 0081](../adr/0081-web-local-day-timeline-grouping.md).


## Tag in combinazione con la fonte (#184)

Puoi aggiungere un tag alla sorgente scelta; entrambi
devono corrispondere e i tag si estraggono da tutte le
note già caricate, indipendentemente dalla fonte scelta.
Si mantiene il cursore e la stessa policy di sessione.
[Guida tag](timeline-tag-filter.md) · ADR 0082.
## Combinazione con la giornata (#185)

La fonte può essere abbinata a un singolo giorno locale delle pagine già caricate; la selezione del giorno è indipendente dall'elenco delle fonti. Vedi [guida giorno](timeline-day-filter.md).
