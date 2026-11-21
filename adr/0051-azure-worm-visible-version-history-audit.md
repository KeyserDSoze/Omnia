# ADR 0051 — Azure Blob visible history ambiguity audit (read-only, NOT anti-rollback)

**Data:** 2026-11-21  
**Stato:** Accepted as an Azure SDK scripted-HTTP diagnostic slice only; NOT production authority.  
**Issue:** #152, child of #144/#141, follows #146/#151 (ADRs 0045/0050), parity with #150 S3.

## Problema e decisione

Azure Blob version-level immutable storage is **per version**. Versioning
permits new versions and deletion/soft-delete of the current name; the
historical exact VersionId may still have a verifiable Locked policy.
An exact-version receipt alone cannot imply a globally once-ever claim.
Azure's `List Blobs` exposes only metadata visible to that same account
and its administrator; it does not certify complete external history.

Add a read-only `AzureBlobSdkVersionHistory` and
`AzureBlobWormHistoryAudit` to enumerate the **visible** catalogue
for a single evidence key. Use Azure.Storage.Blobs
`GetBlobsAsync(BlobTraits.None, BlobStates.Version | BlobStates.Deleted |
BlobStates.DeletedWithVersions | BlobStates.Snapshots, prefix:exactKey)`, one page at a time
(max 2 entries and 8 pages), with a bounded, nonrepeating continuation
cursor. The SDK adapter makes **no writes** and never uses or
deletes the mutable `latest` version.

Reject with `Uncertain` (without readback) if any entry is missing,
duplicate, a different version/current alias, a soft-deleted version or
root, a snapshot, foreign key, invalid UTC VersionId, oversized page,
cyclic/truncated/invalid cursor, 403, timeout or provider error.
Only a single visible matching current version permits readback via the
previously tested `AzureBlobWormAttemptReader` on the exact externally
provided VersionId; this must pass actor HMAC, store identity, locked
immutability policy and future retention.

Even if `SingleVisibleVersionVerified=true`, **always**:
`MayAutoRetry=false`, `MayReleaseRecovery=false`,
`ProvesCompleteHistory=false`, `ProvesMonotonicHistory=false`,
`ProvesSourceDeleteCommit=false`. The page cap and SDK listing cannot
prove that omitted entries do not exist. No API, worker, DELETE,
retry, primary DB commit or recovery-ready link.

## TDD and validation

- Tests first `tests/Omnia.AzureBlobWorm.Tests/AzureBlobVersionHistoryAuditTests.cs` exercise single visible good, competing versions, duplicate/missing, deletion/snapshot, foreign key, invalid version, paging loop/truncation/missing pages, simulated provider 403 and wrong actor, plus **real Azure SDK** List Blobs HTTP serialization through a scripted in-memory responder.
- RED test-first commit `685a68e2`, [CI](https://github.com/KeyserDSoze/Omnia/actions/runs/44914308365). Check run status before claiming RED success.
- Implementation commit `3f36ce26`, [CI](https://github.com/KeyserDSoze/Omnia/actions/runs/44914362707). Check results and any corrections before final acceptance.
- Final HEAD CI, OIDC and Pages must all succeed before closing #152.

## Threat model, privacy and remaining P0

The test responder is **not** the Azure service, Azurite WORM,
production IAM, Key Vault, offsite independent custody or an
out-of-band monotonic checkpoint. A compromised administrator/provider
can omit entries or change a listing; even one returned Locked version
is not a global uniqueness proof. UTC clock is provided by the caller,
not trusted time. No source PostgreSQL DELETE COMMIT is observed. All
true multi-PITR/erasure and GDPR controls remain in #144/#141/#109/#31,
which MUST stay open. UUID and HMACs remain pseudonymous personal
data to evaluate under GDPR; not a categorical exemption.

## Fonti ufficiali

- [Azure SDK list blob versions](https://learn.microsoft.com/en-us/azure/storage/blobs/versions-manage-dotnet)
- [List Blobs REST: include versions, deleted, deletedwithversions](https://learn.microsoft.com/en-us/rest/api/storageservices/list-blobs)
- [BlobStates](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.models.blobstates)
- [Azure immutable version WORM policies](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-version-level-worm-policies)
- [GDPR 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/)
