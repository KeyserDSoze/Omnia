# ADR 0019 — Gate di quarantena dell'API dopo un recovery

**Data:** 2026-11-19  
**Stato:** Accepted per primo slice di #141; NON è un journal di cancellazioni o un release gate production-ready.

## Motivazione

Un ripristino PITR può riportare a uno stato antecedente a una cancellazione valida (ADR 0017). ADR 0018 prova il replay con una ricevuta sintetica, ma non garantisce completezza del journal né impedisce un riavvio prematuro dell'API. Occorre almeno un controllo meccanico del traffico che dipenda da stato amministrato fuori dal database ripristinato.

## Decisione implementata

- RecoveryQuarantineGate legge l'opzione `Recovery:AccessStateFile` (variabile `Recovery__AccessStateFile`): percorso assoluto di un file riservato controllato fuori da PGDATA e dai backup del DB.
- In ambienti diversi da Development il percorso e il file sono OBBLIGATORI. In Development soltanto, assenza dell'opzione lascia funzionare gli ambienti di test esistenti; se è esplicitata, la verifica si applica comunque.
- Il file non può essere symlink, deve essere inferiore o uguale a 32 byte, e su Unix solo il proprietario può leggerlo; nessun accesso group/other. Solo gli esatti 6 byte `ready\n` permettono l'avvio; `quarantined\n`, errori, file mancanti e formati diversi bloccano tutto.
- La verifica precede datasource PostgreSQL, migrazioni e creazione dei servizi. Il processo fallisce con il solo codice diagnostico `RECOVERY_QUARANTINED`, senza stampare il percorso del file o dati sensibili.
- A ogni richiesta HTTP, prima dell'autenticazione e della logica applicativa, il file viene ricontrollato. In caso di stato non autorizzato risponde 503, JSON `recovery_quarantined` e `Cache-Control: no-store`. Ripristinando `ready\n` il traffico HTTP può proseguire.
- Test xUnit negativi coprono mancanza, file non valido, symlink, permessi, Development/Production e cambi di stato. La CI esegue l'entrypoint reale senza DB: deve fallire per recovery in quarantena, non per mancata configurazione DB. OIDC E2E avvia l'API reale e controlla 503 su /health e /api/v1/memories quando il file viene revocato.

## Limitazioni di sicurezza e operatività

Il valore `ready\n` è **soltanto una dichiarazione dell'operatore**, non un certificato firmato: non prova autorizzazione, completezza del journal esterno, high-watermark anti-rollback, cancellazione dopo PITR o consenso valido. Chi può modificare il file può fingere lo stato ready. Il gate non tutela repliche senza identica configurazione, non interrompe richieste già in volo e **non ferma i worker di retention già avviati, i worker offline o le azioni esterne**. Non si garantisce che il marker sia stato ricreato in quarantena dopo un guasto dell'intero host. L'orchestratore dovrà bloccare ingress e tutti i workload fino a una release firmata e verificata.

L'esecuzione su GitHub Actions resta sintetica e senza dati personali reali. #141, #31, #109, #125 e #140 restano OPEN. Questa implementazione NON autorizza il rilascio di un recovery o una dichiarazione di conformità GDPR.

## Riferimenti autorevoli

- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/startup?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0
- https://www.postgresql.org/docs/17/continuous-archiving.html
- https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/