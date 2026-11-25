# Conservazione e pulizia automatica

| Tabella | Scadenza | Calcolo |
|---|---|---|
| `memory_entries` | 30 giorni | da `received_at` (orario del server); un retry idempotente non la estende |
| `triage_decisions` | 30 giorni | dall'ultima decisione sulla carta |
| `connector_actions` | 180 giorni | dalla richiesta dell'azione |
| `connector_authorizations` | 10 minuti | dall'avvio del collegamento; consumata al primo uso |

Le righe scadute non sono più restituite dalle letture anche prima della pulizia; l'export include
quelle ancora presenti con la loro scadenza.

## Worker

- Gira nel processo API (`RetentionSweepService`): subito all'avvio, poi ogni 15 minuti.
- Usa il ruolo `tao_retention`, distinto da API e migrazioni, che ha **solo** `EXECUTE` su
  `tao_purge_expired_memories(integer)` e `tao_purge_expired_work_state(integer)`: funzioni
  `SECURITY DEFINER` con SQL fisso, lotti 1–500, `FOR UPDATE SKIP LOCKED` (più istanze sono sicure).
  All'avvio l'API verifica che il ruolo non possa leggere né modificare le tabelle personali.
- I log riportano solo conteggi aggregati.

## Limiti

- La durata di 30 giorni è una scelta di prodotto, non un obbligo normativo: valutala per il tuo uso
  (GDPR art. 5(1)(e)).
- Backup, log dell'host e dati presso i provider seguono le loro regole.
- Se l'API è spenta la pulizia è rimandata, ma le righe scadute restano invisibili.
