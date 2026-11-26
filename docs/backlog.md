# Backlog

Fonte di verità: le issue GitHub. Gli epic della nuova fase Tao sono:

| # | Epic | Release |
|---|---|---|
| [#221](https://github.com/KeyserDSoze/Tao/issues/221) | Installazione on-prem: gateway 59800, Keycloak, Cloudflare Tunnel, backup | R1 |
| [#222](https://github.com/KeyserDSoze/Tao/issues/222) | Microsoft 365 e Google Workspace: lettura e azioni confermate | R1 |
| [#223](https://github.com/KeyserDSoze/Tao/issues/223) | Oggi: carte da smistare fatto / non fatto / dopo / scarta | R1 |
| [#224](https://github.com/KeyserDSoze/Tao/issues/224) | Piano del giorno e pre-azioni con modello OpenAI-compatibile | R1 |
| [#225](https://github.com/KeyserDSoze/Tao/issues/225) | Uso vocale in cuffia | R2 |
| [#226](https://github.com/KeyserDSoze/Tao/issues/226) | Ping e notifiche | R2 |
| [#227](https://github.com/KeyserDSoze/Tao/issues/227) | Memoria giornaliera da mail, chat, Teams, transcript e CRM | R2 |
| [#228](https://github.com/KeyserDSoze/Tao/issues/228) | Flussi di business e pre-azioni definiti dall'utente | R2 |
| [#229](https://github.com/KeyserDSoze/Tao/issues/229) | Profilo di stile comunicativo opt-in | R2 |
| [#230](https://github.com/KeyserDSoze/Tao/issues/230) | Client desktop Mac/Windows e dashboard | R3 |
| [#239](https://github.com/KeyserDSoze/Tao/issues/239) | Integrazioni: catalogo, server MCP, API dirette e WhatsApp ([ADR 0111](../adr/0111-integration-catalog-mcp-api-whatsapp.md), [studio](integrations.md)) | R2 |
| [#240](https://github.com/KeyserDSoze/Tao/issues/240) | Identity provider proprio (sostituisce Keycloak) e server MCP di Tao per altre AI ([ADR 0112](../adr/0112-own-identity-provider-and-mcp-server.md)) | R2 |

Le issue della fase precedente ancora aperte (#1–#131) descrivono una piattaforma SaaS più ampia:
restano come riferimento e vanno ricollegate a un epic Tao quando entrano in lavorazione. Quelle del
filone disaster recovery e supply-chain sono state chiuse come *not planned* (ADR 0098).

## Regole

- Una funzionalità non banale ha una issue sotto un epic, criteri di accettazione verificabili e
  test che falliscono prima dell'implementazione.
- Chiudere una issue solo con evidenza (test eseguiti, commit, eventuale run CI).
