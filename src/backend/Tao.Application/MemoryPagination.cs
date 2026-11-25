using System.Globalization;
using System.Text;

namespace Tao.Application;

/// <summary>
/// A bounded keyset position, NOT an authorization credential or snapshot.
/// PostgreSQL sorts by occurred_at DESC, id DESC. A canonical UTC ticks + UUID
/// token prevents ambiguous dates, locale parsing and accidental offsets.
/// </summary>
public sealed record MemoryPageCursor(DateTimeOffset OccurredAt, Guid Id)
{
    public string Encode()
    {
        var bytes = Encoding.ASCII.GetBytes(
            $"{OccurredAt.UtcTicks:D19}:{Id:N}");
        return Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? token, out MemoryPageCursor? cursor)
    {
        cursor = null;
        // Exactly 52 plain ASCII bytes => 70 base64url chars, no padding.
        if (token is null || token.Length != 70 ||
            token.Any(c => !((c >= 'A' && c <= 'Z') ||
                              (c >= 'a' && c <= 'z') ||
                              (c >= '0' && c <= '9') || c is '-' or '_')))
            return false;

        try
        {
            var encoded = token.Replace('-', '+').Replace('_', '/') + "==";
            var raw = Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            if (raw.Length != 52 || raw[19] != ':' ||
                !long.TryParse(raw.AsSpan(0, 19), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
                !Guid.TryParseExact(raw.AsSpan(20), "N", out var id) ||
                id == Guid.Empty)
                return false;
            var parsed = new MemoryPageCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero), id);
            if (!string.Equals(parsed.Encode(), token, StringComparison.Ordinal))
                return false; // non-canonical base64 / casing is rejected
            cursor = parsed;
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}

public sealed record MemoryPage(
    IReadOnlyList<MemorySummary> Items, string? NextCursor);

public interface IMemoryPageRepository
{
    Task<MemoryPage> PageAsync(string tenantId, string ownerSubject,
        int limit, MemoryPageCursor? before, CancellationToken ct);
}

public interface IMemoryPageCatalog
{
    Task<MemoryPage> PageAsync(string tenantId, string ownerSubject,
        int? limit, string? before, CancellationToken ct);
}

/// <summary>
/// Validation happens before SQL/JSON/cryptography. The cursor never carries
/// tenant/owner, and is never trusted to authorize a read.
/// </summary>
public sealed class MemoryPageCatalog(IMemoryPageRepository repository) : IMemoryPageCatalog
{
    public Task<MemoryPage> PageAsync(string tenantId, string ownerSubject,
        int? limit, string? before, CancellationToken ct)
    {
        var size = limit ?? 10;
        if (size is < 1 or > 50)
            throw new ArgumentException("Invalid page size.", nameof(limit));

        MemoryPageCursor? cursor = null;
        if (before is not null && !MemoryPageCursor.TryDecode(before, out cursor))
            throw new ArgumentException("Invalid cursor.", nameof(before));

        return repository.PageAsync(tenantId, ownerSubject, size, cursor, ct);
    }
}
