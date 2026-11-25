namespace Tao.Application;

/// <summary>
/// A snapshot export and erasure of the personal-data tables currently owned by Tao.
/// No external connectors, provider model histories or backups are covered by this port.
/// Tenant and subject MUST be obtained from a verified identity, never the request body.
/// </summary>
public interface IPersonalDataService
{
    Task WriteExportAsync(string tenantId, string ownerSubject, Stream destination,
        CancellationToken cancellationToken);
    Task<PersonalDataErasureResult> EraseAsync(string tenantId, string ownerSubject,
        CancellationToken cancellationToken);
}

public sealed record PersonalDataErasureResult(
    int DeletedMemories, int DeletedConsents, DateTimeOffset CompletedAt);
