# Azure PaaS — template opzionale

**Stato:** punto di partenza Bicep **non validato** con `az deployment` e non mantenuto in parità con
lo stack on-prem. La prima release di Tao gira on-prem con Docker Compose (vedi README e ADR 0098).

Risorse dichiarate in `main.bicep`: Log Analytics, Azure Container Registry, Storage, Key Vault,
Service Bus, PostgreSQL Flexible Server con `vector`, Container Apps environment.

Prima di usarlo servirebbero almeno: rete privata (VNet, private endpoint, DNS), managed identity
con `AcrPull`, segreti in Key Vault (key ring, client secret dei connettori, chiave del modello),
`Auth__Issuer`/`Auth__Audience` HTTPS, `Tao__PublicBaseUrl` HTTPS, i tre ruoli PostgreSQL separati
(`tao_app`, `tao_retention`, owner delle migrazioni) e l'estensione `vector` abilitata.
