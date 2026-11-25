using Tao.Domain;

namespace Tao.Application;

/// <summary>
/// A versioned bounded ledger position, never an actor selector or credential.
/// This is intentionally not interchangeable with memory-page tokens.
/// </summary>
public sealed record ConsentPageCursor(DateTimeOffset GrantedAt, Guid Id)
{
    private const string Prefix = "c1.";

    public string Encode() =>
        Prefix + new MemoryPageCursor(GrantedAt, Id).Encode();

    public static bool TryDecode(string? token, out ConsentPageCursor? cursor)
    {
        cursor = null;
        if (token is null || token.Length != 73 ||
            !token.StartsWith(Prefix, StringComparison.Ordinal) ||
            !MemoryPageCursor.TryDecode(token[Prefix.Length..], out var position) ||
            position is null)
            return false;
        cursor = new ConsentPageCursor(position.OccurredAt, position.Id);
        return true;
    }
}

public sealed record ConsentPage(IReadOnlyList<ConsentGrant> Items, string? NextCursor);

public interface IConsentPageRepository
{
    Task<ConsentPage> PageAsync(string tenantId, string ownerSubject,
        int limit, ConsentPageCursor? before, CancellationToken ct);
}

public interface IConsentPageService
{
    Task<ConsentPage> PageAsync(string tenantId, string ownerSubject,
        int? limit, string? before, CancellationToken ct);
}

/// <summary>
/// Validate request bounds BEFORE a scoped PostgreSQL read.
/// Authorization still comes exclusively from signed actor and FORCE RLS.
/// </summary>
public sealed class ConsentPageService(IConsentPageRepository repository)
    : IConsentPageService
{
    public Task<ConsentPage> PageAsync(string tenantId, string ownerSubject,
        int? limit, string? before, CancellationToken ct)
    {
        var size = limit ?? 25;
        if (size is < 1 or > 50)
            throw new ArgumentException("Invalid consent page size.", nameof(limit));

        ConsentPageCursor? cursor = null;
        if (before is not null && !ConsentPageCursor.TryDecode(before, out cursor))
            throw new ArgumentException("Invalid consent page cursor.", nameof(before));

        return repository.PageAsync(tenantId, ownerSubject, size, cursor, ct);
    }
}
