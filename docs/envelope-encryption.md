# Omnia — Envelope encryption v1 (base crittografica #137)

**Stato aggiornato al 2026-11-18:** codec testato e integrato per i **nuovi raw/normalized/summary/tags** su PostgreSQL (#139); il backfill per attori approvati è disponibile ma **non è stato eseguito sull'intero database** (#140). Restano scoperti vettori, metadati sensibili, media, token, backup/WAL e vault. La parent #33 è aperta e l'uso di dati veri resta vietato fino ai gate privacy e security.

## Contratto di libreria

- `Omnia.Application.IEnvelopeEncryption`: `Seal`, `Open` e `Rewrap`, con `EncryptionScope(TenantId,OwnerSubject,Purpose,RecordId)`.
- `Omnia.Infrastructure.AesGcmEnvelopeEncryption`: AES-256-GCM con chiave casuale DEK **diversa per payload**, nonce 96 bit per DEK wrapping e payload (generati con `RandomNumberGenerator`), tag autenticazione 128 bit, KEK esterna per ID/versione.
- La associated data (`AAD`) è composta da tuple lunghezza+UTF-8 di tenant, subject, purpose e record (più domain separation tra cifratura e wrapping; ID KEK autentificato nel wrapping). In presenza di subject o scope errato o di qualsiasi modifica a tag/ciphertext l'operazione fallisce.
- Formato `OMN1`: 4 byte magic/version; 1 byte lunghezza ID; ID ASCII; 12 byte nonce wrapping; 16 byte tag wrapping; 32 byte DEK cifrata; 12 byte nonce dati; 16 byte tag dati; ciphertext. `Rewrap` prima convalida il dato, poi cambia solo KEK ID e blocco di wrapping; il ciphertext rimane inalterato. La KEK nuova non rende subito inutilizzabile quella vecchia: conservarla fino alla verifica di **tutti** i record persistiti e backup.
- Limiti v1: al massimo 1 MiB per `Seal` in memoria e 16 chiavi in un ring; **non** usarlo per media streaming o big data. AES-GCM richiede attenta gestione collisioni dei nonce e rotazione secondo il volume d'uso; valutare il protocollo prima di scala elevata. I buffer DEK e le copie temporanee KEK sono azzerati dopo l'uso (best effort in managed memory).

## Key ring esterno (solo sviluppo e test)

Non esistono chiavi predefinite o segreti in Git. Il formato di un file JSON montato **fuori dal repository**, con percorso assoluto, è:

```json
{
  "activeKeyId": "local-v1",
  "keys": {
    "local-v1": "<base64 di 32 byte casuali>"
  }
}
```

Generare una chiave crittograficamente casuale, p.es. `openssl rand -base64 32`, e conservare il file con permessi stretti in un secret mount o vault, non nel bundle Web/Expo, issue, Actions artifact o log. Invocare `EnvelopeKeyRing.FromFile(absolutePath)` da un processo autorizzato, oppure `FromBase64(activeId, keysFromTrustedKms)` dopo l'acquisizione da provider fidato. Il loader rifiuta path relativi, file troppo grandi, JSON malformato, ID fuori whitelist e KEK non esattamente 32 byte; non include chiavi nei messaggi di errore. L'API ora carica obbligatoriamente il file esterno tramite `Crypto:KeyRingPath`; in sua assenza fallisce all'avvio. Nessuna KEK entra nel bundle mobile/browser. L'uso della keyring su file rimane solo un adapter di sviluppo/test, non un vault production-ready.

## Rotazione e recovery

1. Provisionare una **nuova** KEK con diverso ID, continuando a fornire entrambe le versioni al ring.
2. Cambiare `activeKeyId` per tutte le *nuove* cifrature; i vecchi payload rimangono decrittabili solo finché è disponibile la vecchia KEK.
3. Per i payload esistenti eseguire `Rewrap(envelope, sameTrustedScope)`, con write atomico e controlli di integrità/audit nel futuro repository (non ancora disponibile). Il codec autentica il vecchio payload prima di modificare il wrapping; una seconda `Rewrap` con l'ID già attivo restituisce lo stesso envelope.
4. Verificare restore, replicas, retention, backup e data-access, poi programmare l'eliminazione della vecchia KEK solo quando **nessun** oggetto dipende da essa e la policy legale consente la revoca. Test xUnit coprono roundtrip e restore serializzato, non un vault reale.

## Restante lavoro obbligatorio di #33

Inventario campi sensibili `raw_content`, `normalized_json`, `summary`, `tags`, `embedding` (pgvector ricerca vs ciphertext), consensi, audio device, credenziali connettori, token OAuth, log e backup. Per ogni store progettare cifratura autentificata, backfill transazionale non espositivo, RLS, esporto portabile, cancellazione e TTL; integrazione Key Vault/OpenBao e workload identity; test di migrazione e recovery, controllo delle chiavi, scansione CI di segreti e sicurezza endpoint. Le nuove note redigono i quattro campi e salvano la busta cifrata; **le righe legacy non ancora migrate possono restare in chiaro**. Anche le colonne `embedding`, `source`, `external_id`, i timestamp e altri metadati, così come i backup precedenti, non sono protetti da questo codec. Non dichiarare che tutti i dati siano `encrypted at rest`.

## Riferimenti

- OWASP [Cryptographic Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- OWASP [Key Management Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html)
- Microsoft [.NET 10 AesGcm constructors/tag size](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.-ctor?view=net-10.0)


## Evidence di ripristino sintetico (#140, ADR 0015)

La CI esegue ora `pg_dump -Fc` e `pg_restore` PostgreSQL reale verso un secondo database nello **stesso cluster usa-e-getta**, prova l'apertura della busta con vecchia KEK mantenuta nel ring e il fallimento se la KEK storica manca. Verifica anche export, RLS, retention e cancellazione nel clone. Per dettagli e limiti consultare [docs/restore-rehearsal.md](restore-rehearsal.md). Questa prova non include PITR/WAL, cluster Azure separato, gestione di chiavi in vault o cancellazione da backup reali.
