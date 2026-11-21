# ADR 0050 — Azure Blob one-shot upload, explicit version lock, fail-closed readback

**Data:** 2026-11-21  
**Stato:** Accepted as synthetic Azure SDK protocol slice ONLY; not production-ready.  
**Issue:** #151 (parent #144/#141, follow-up #146/ADR 0045, parity with #147/ADR 0046).

## Decision

Azure Blob version-level WORM differs from S3 Object Lock. A custom
immutability policy on upload may be **Unlocked**, even when a locked
container policy exists. Hence the writer does not mistake a successful
PUT for a completed lock.

1. Validate the actor-bound HMAC receipt before network calls. The only
   allowed key is `erasure-attempts/v1/{requestId:N}.json`, with UUID
   generated outside the storage adapter. Validate UTC expiration
   **strictly beyond one hour** and supply no tenant/owner plaintext
   in the blob's content.
2. An SDK client internally built with `BlobClientOptions.Retry.MaxRetries=0`
   performs a single conditional upload (`If-None-Match: *`) carrying
   an **Unlocked** custom version-level retention policy. Even a successful
   upload does not provide evidence of WORM or source DELETE COMMIT.
3. Require an exact UTC version identifier from the upload ACK. Call
   `WithVersion(versionId).SetImmutabilityPolicyAsync(Locked)` with the
   same expiry. NEVER call `latest`, retry an uncertain upload or lock,
   or delete a potentially committed prior version.
4. After the lock call, `AzureBlobWormAttemptReader` performs HEAD+GET
   pinned to that same exact VersionId, verifies effective Locked mode,
   UTC retention, consistent ETag/content size, and two-key HMAC actor
   binding through `ImmutableAttemptEvidence.VerifyAsync`. A result is
   observed only if the effective expiry is not materially shorter than
   the requested expiry (subsecond timestamp encoding tolerance).
5. On missing/rejected version, 403/409/412, timeout, cancellation,
   policy mismatch, corruption, upload ACK loss or lock denial, return
   `Uncertain` and never automatic retry/replay/DELETE/recovery ready.
   Invalid actor/bytes/keys/retention must be rejected **before** upload.

## Threat model and limits

The operation has an unavoidable **two-step gap** between upload
and lock. A hostile administrator or timeout can leave an unlocked
version; the code cannot safely infer absence or perform compensation.
A locked version can coexist with another new version under the same
mutable blob name; `If-None-Match` is not a global high-watermark.
The caller must retain its exact VersionId independently of restorable
databases. Azure's `Set Blob Immutability Policy` requires special
RBAC rights; these credentials must not be granted to the runtime API.

Tests use **real Azure.Storage.Blobs SDK HTTP serialization** with
an in-memory scripted responder (NOT Azure service). They are not
proof of external immutable custody, distinct owner/admin/IAM,
managed identity, immutable storage configuration, account/version
lock behavior, survival of simultaneous PITR, monotonic history,
DELETE COMMIT, key vault/HSM or GDPR compliance. #144/#141/#109/#31
remain OPEN. No production adapter registration, write-capability
grant, actual user data, replay or recovery release in this slice.

## TDD evidence

- **RED confirmed**: test-first commit [`8d0311f2`](https://github.com/KeyserDSoze/Omnia/commit/8d0311f248fac517981fcfec75c0a168e9c04cff), [CI 44911802354](https://github.com/KeyserDSoze/Omnia/actions/runs/44911802354): FAILURE CS0246 for intentionally missing `IAzureBlobWormAttemptWrite` and `AzureBlobWormWriteAck`.
- **First GREEN attempt**: implementation [`30bda705`](https://github.com/KeyserDSoze/Omnia/commit/30bda705178f35650074be5f90b3a49f7531267b), [CI 44911845309](https://github.com/KeyserDSoze/Omnia/actions/runs/44911845309): compiled, but scripted HTTP PUT response used 200 rather than Azure's expected Created (201); positive test failed.
- **GREEN verified**: [`2a871ce3`](https://github.com/KeyserDSoze/Omnia/commit/2a871ce3569aaeac2de3fcb6209ba2765e4b4140) fixed scripted responder, [CI 44911945201](https://github.com/KeyserDSoze/Omnia/actions/runs/44911945201) **SUCCESS**: 8/8 Azure scripted SDK tests, 16/16 S3 real SeaweedFS protocol tests, 19/19 Domain, 80/80 PostgreSQL Integration, all PITR/restore and Docker build, mobile job.
- [OIDC real-browser E2E 44911945246](https://github.com/KeyserDSoze/Omnia/actions/runs/44911945246) and [GitHub Pages 44911945268](https://github.com/KeyserDSoze/Omnia/actions/runs/44911945268): both **SUCCESS** at same SHA 2a871ce3.
- The final documentation-only follow-up after this evidence requires a fresh full-main Actions check before #151 is closed. #144/#141/#109/#31 remain OPEN; green CI is NOT Azure live WORM or offsite custody.

## Official sources

- [Microsoft: Version-level WORM policy, custom upload may remain Unlocked](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies).
- [BlobUploadOptions SDK](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.models.blobuploadoptions).
- [Set Blob Immutability Policy REST and RBAC](https://learn.microsoft.com/en-us/rest/api/storageservices/set-blob-immutability-policy).
- [Azure BlobClient exact version API](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.blobclient).
- [GDPR EU 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng).
