# Cifratura a busta (envelope encryption)

## Contratto

- `Tao.Application.IEnvelopeEncryption`: `Seal`, `Open`, `Rewrap` con
  `EncryptionScope(TenantId, OwnerSubject, Purpose, RecordId)`.
- `Tao.Infrastructure.AesGcmEnvelopeEncryption`: AES-256-GCM, DEK casuale per payload, nonce 96 bit,
  tag 128 bit, DEK cifrata con la KEK attiva del key ring.
- **AAD** = tenant, soggetto, scopo e id del record (codificati con lunghezza), con separazione di
  dominio tra dati e wrapping. Un ciphertext usato con un altro scope o un'altra riga non si apre.
- Formato `TAO1`: magic/versione (4 byte), lunghezza e id KEK, nonce e tag del wrapping, DEK cifrata,
  nonce e tag dei dati, ciphertext. Limiti: 1 MiB per payload, 16 chiavi nel ring.

## Scopi in uso

| Scopo | Record | Contenuto |
|---|---|---|
| `memory.payload` | id nota | testo originale, JSON normalizzato, sommario, tag |
| `connector.authorization` | hash dello state OAuth | verifier PKCE |
| `connector.credentials` | id account collegato | token OAuth, scope, identità (id, nome, email) |

Restano in chiaro (servono a indici e RLS): tenant, soggetto, sorgente e id esterno delle note,
timestamp, embedding, provider e id esterno degli account, chiavi delle carte e decisioni.

## Key ring

File JSON montato in sola lettura (`Crypto:KeyRingPath`, in compose `docker/secrets/envelope.keyring.json`):

```json
{ "activeKeyId": "k1", "keys": { "k1": "<base64 di 32 byte casuali>" } }
```

`docker/setup.*` lo genera con `openssl rand -base64 32` / `RandomNumberGenerator`. Senza key ring
l'API non parte. **Fanne il backup separatamente dal database**: senza di esso note e credenziali non
sono recuperabili; insieme al dump del DB, invece, danno accesso a tutto.

## Rotazione

1. Aggiungi una nuova chiave al ring (nuovo id) e impostala come `activeKeyId`: le nuove scritture
   usano la nuova KEK, le vecchie restano leggibili finché la vecchia chiave è nel ring.
2. Esegui `Rewrap` sui record esistenti (cambia solo il wrapping della DEK; il ciphertext resta).
   Uno strumento di rewrap in blocco non è ancora disponibile (epic #222).
3. Rimuovi la vecchia chiave solo quando nessun record e nessun backup ne dipende.

Riferimenti: OWASP [Cryptographic Storage](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
e [Key Management](https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html) cheat sheet.
