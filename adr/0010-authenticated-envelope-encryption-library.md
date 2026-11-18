# ADR 0010 — Envelope AES-256-GCM con AAD e key wrapping

**Data:** 2026-11-18  
**Stato:** Accepted for codec-only slice #137. NON autorizza l'uso di dati reali né chiude #33.

## Problema

L'attuale modello PostgreSQL conserva raw, JSON normalizzato, summary e embedding in colonne non cifrate a livello applicativo. La sola protezione disco/trasporto non separa il danno da un dump non autorizzato. Occorre definire un contratto testabile per l'envelope encryption prima di qualsiasi migrazione di tabelle, evitando un algoritmo proprietario o credenziali statiche.

## Decisione

`IEnvelopeEncryption` in Application espone `Seal`, `Open`, `Rewrap` con `EncryptionScope` (tenant, subject, finalità, ID canonico del record) fornito da un contesto autenticato. Infrastructure implementa `AesGcmEnvelopeEncryption` con payload cifrato usando un DEK nuovo di 256 bit e tag GCM 128 bit; il DEK viene wrapped da una KEK 256-bit versionata, sempre con GCM 128-bit e nonce casuali separati 96-bit. Il formato binario `OMN1` contiene il key ID, non la KEK, e i parametri necessari per la decodifica. La AAD ha campi prefissati in lunghezza e separazione dei domini, per evitare collisioni tra scope, tenant e record. Errori di autenticazione, ID sconosciuti, formati o chiavi invalidi non producono plaintext. I buffer temporanei di DEK e KEK sono azzerati best effort.

`EnvelopeKeyRing.FromFile` usa path assoluto di file locale esterno e non crea chiavi di default; `FromBase64` permette in futuro l'iniezione di KEK da vault esterni. `Rewrap` verifica il tag dati prima di re-wrappare il DEK con la KEK attiva, senza ricifrare il payload originale, per migrazione di key version. Limite v1: 1 MiB payload sincrono, 16 KEK. Test reali xUnit per roundtrip, AAD cross-tenant, tamper, JSON key ring, restore, rotazione e fail closed.

## Alternative e tradeoff

Non usare cifratura deterministica né password in repo/Expo; non trasferire arbitrariamente chiavi nel SQL. La KEK da file è supporto per test/operatori, non sostituisce Key Vault, OpenBao o HSM. Non introdurre un ciphertext accanto a colonne plaintext fingendo una reale protezione. La futura migrazione richiede un piano per raw+derivati/embedding, RLS, export/erase/TTL e recovery; l'indice pgvector non è direttamente compatibile con un embedding semplicemente cifrato. Per media grandi serve streaming authenticated encryption con chunk security, non questo codec.

Il codec **non è registrato nell'API e non ha alcun effetto sulla persistenza corrente**: nessuna feature di storage cifrato è dichiarata pronta; #33 rimane aperta, insieme a #31/#32 e #65.

## Riferimenti

OWASP Cryptographic Storage e Key Management Cheat Sheets; Microsoft .NET 10 `AesGcm` con tag size esplicito; vedi `docs/envelope-encryption.md`. 
