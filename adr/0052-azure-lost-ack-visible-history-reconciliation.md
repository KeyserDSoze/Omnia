# ADR 0052 — Azure lost-ACK WORM reconciliation from visible history, no replay authority

**Data:** 2026-11-21  
**Stato:** Accepted only for synthetic read-only protocol validation; NOT a production anti-rollback authority.  
**Issue:** #153, child of #144/#141, follow-up #151/#152 (ADRs 0050/0051), parity with #148 S3.

## Problema

Azure Blob version-level WORM protects a **specific** version, and an
upload may commit without its HTTP acknowledgement being delivered.
A custom policy may remain Unlocked before an explicit
`SetImmutabilityPolicy(Locked)`, so ACK loss at any step is **not**
a safe reason to retry. The caller may know the UUID and HMAC keys
but not the VersionId because the original response was lost.
Neither `latest` nor a missing provider-list entry proves absence.

## Decisione (child #153)

Provide a separate **read-only** `AzureBlobLostAckHistoryReconciler`
using the preexisting `IAzureBlobVersionHistory` adapter from ADR 0051
and `IImmutableAttemptObjectReader` from ADR 0043/0045.
No write interface, REST DELETE, storage policy alteration, retries,
worker, PostgreSQL actor state mutation or recovery release.

The operation requires a canonical UUID, externally authenticated
tenant/subject, TWO distinct 32-byte HMAC keys and an explicit UTC
clock. Invalid data is rejected before provider I/O.

It scans the exact `erasure-attempts/v1/{uuid:N}.json` key, **max
2 entries per page and max 8 pages**, with complete page traversal,
bounded nonrepeating continuation cursors. Refuse empty/oversized
pages, a missing response, duplicate/second/foreign keys or
versions, missing or alias VersionId (`latest`/`null`), non-UTC
VersionId, snapshot, soft-delete, noncurrent versions, 403/timeout,
truncated or looping pagination. Refuse any incomplete catalogue
WITHOUT interpreting it as 'no attempt'. An empty continuation page
is conservative `Uncertain`.

A **single visible** candidate VersionId permits an exact-version
`ImmutableAttemptEvidence.VerifyAsync` readback: check actor-bound
canonical HMAC, configured store identity, version-pinned Azure
`Locked` effective retention **more than one hour from the
supplied UTC clock**, and bounded, unmodified bytes. A missing
lock between PUT and commit remains `Uncertain`.

An `AzureLostAckHistoryOutcome` sets `PreviouslyObserved=true`
only for the candidate validated under these conditions; in ALL
outcomes `MayAutoRetry=false`,
`MayReleaseRecovery=false`,
`ProvesSourceDeleteCommit=false`,
`ProvesCompleteHistory=false`,
`ProvesMonotonicHistory=false`.

## Tests and boundaries

`AzureLostAckReconcileTests` simulates a single successful remote
commit with an intentionally lost ACK. The original publisher returns
`Uncertain` and calls the writer once; the read-only reconciler may
observe the matching authenticated Locked version but cannot
authorize a second write. Tests reject alternate version, duplicate,
snapshot, deleted/noncurrent, foreign key/account/actor, wrong MAC,
Unlocked/S3 mode, short expiry, invalid clock/keys and interrupted
provider pagination. This is a **FAKE storage history/reader with
synthetic HMAC bytes**, not a live Azure account. The previously
implemented Azure SDK serialized HTTP version-list, HEAD/GET and
version-lock tests are regression-gated by the same CI project.

TDD RED commit `e58c9b60`:
[GitHub CI 44915879106](https://github.com/KeyserDSoze/Omnia/actions/runs/44915879106)
**expected failure CS0246** (new `AzureBlobLostAckHistoryReconciler`
absent). First implementation commit `f4c527e4`:
[GitHub CI 44915938815](https://github.com/KeyserDSoze/Omnia/actions/runs/44915938815)
result pending as of this ADR. Record definitive status in #153
only after verifying same-SHA CI, OIDC and Pages, and do not close
the issue before the final main gates pass.

### Non-goals and security/legality

A single entry in `List Blobs` is under **the same provider/admin**
and may be omitted, replaced, truncated or concurrently superseded.
It is **not** an independently signed monotonic catalogue, not a
durable post-commit source DELETE witness, not replay protection
against simultaneous PGDATA PITR, not IAM/cloud segregation, not
a trusted clock, not an Azure staging WORM proof or GDPR signoff.
The test-only stored UUID/HMAC is pseudonymous, NOT by default
anonymous; live retention purpose/region, deletion and incident
governance remain for review.

Keep parent #144, #141, #109 and #31 OPEN; no actual Azure resources
provisioned, no API/worker/recovery READY authorization added.

## Official references

- [Azure Blob versioning and listing](https://learn.microsoft.com/en-us/azure/storage/blobs/versions-manage-dotnet)
- [Azure List Blobs REST, include + NextMarker](https://learn.microsoft.com/en-us/rest/api/storageservices/list-blobs)
- [Version-level WORM policies; custom upload may be Unlocked](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies)
- [Set Blob Immutability Policy](https://learn.microsoft.com/en-us/rest/api/storageservices/set-blob-immutability-policy)
- [GDPR (EU) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
