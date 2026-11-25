namespace Tao.Application;

/// <summary>
/// A snapshot export and erasure of the personal-data tables currently owned by Tao
/// (notes, consents, connected accounts, triage decisions and provider action audit).
/// Provider-side data (mailboxes, calendars, app consent at Microsoft/Google) and backups are
/// not covered by this port. Tenant and subject MUST come from a verified identity.
/// </summary>
public interface IPersonalDataService
{
    Task WriteExportAsync(string tenantId, string ownerSubject, Stream destination,
        CancellationToken cancellationToken);
    Task<PersonalDataErasureResult> EraseAsync(string tenantId, string ownerSubject,
        CancellationToken cancellationToken);
}

public sealed record PersonalDataErasureResult(
    int DeletedMemories, int DeletedConsents, DateTimeOffset CompletedAt,
    int DeletedConnectorAccounts = 0, int DeletedWorkState = 0);
