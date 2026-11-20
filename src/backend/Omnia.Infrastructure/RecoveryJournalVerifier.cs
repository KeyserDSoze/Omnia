using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// Bounded, read-only verification of a synthetic/external recovery journal.
/// A separate authenticated checkpoint is necessary to detect truncation.
/// Neither HMAC authenticity nor a checkpoint establishes independent durable
/// custody or proves that ALL deletion events were recorded.
/// </summary>
public static class RecoveryJournalVerifier
{
    public const int MaxEntries = 1000;
    public const int MaxJournalBytes = 1024 * 1024;
    public const int MaxAnchorBytes = 4096;
    private const int MaxEntryBytes = 1024;
    // Cross-language version-1 payload bytes: compact UTF-8 JSON, insertion
    // property order, unescaped printable characters such as '+' and '='.
    // UnsafeRelaxed is used ONLY for authenticated offline payload bytes,
    // never for HTML or browser output. Otherwise default System.Text.Json
    // escaping would differ from JSON generators used by recovery operators.
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GenesisDigest { get; } = Convert.ToBase64String(
        SHA256.HashData("omnia-recovery-journal-v1/genesis"u8));

    public static byte[] EntryPayload(int sequence, string eventId,
        string operation, string tenantId, string ownerSubject,
        string previousDigest) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, sequence, eventId, operation,
            tenantId, ownerSubject, previousDigest
        }, CanonicalJson);

    public static byte[] AnchorPayload(int count, string tipDigest) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, count, tipDigest
        }, CanonicalJson);

    public sealed record Verification(int Count, string TipDigest);

    /// <summary>
    /// Fails closed on missing, forged, duplicated, truncated, reordered,
    /// malformed, over-limit or mismatched journal/checkpoint data.
    /// This does NOT apply erasures, authorize API release, or persist state.
    /// </summary>
    public static Verification Verify(ReadOnlySpan<byte> journal,
        ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> journalKey,
        ReadOnlySpan<byte> anchorKey)
    {
        if (journal.Length is < 2 or > MaxJournalBytes ||
            anchor.Length is < 2 or > MaxAnchorBytes ||
            journalKey.Length != 32 || anchorKey.Length != 32 ||
            CryptographicOperations.FixedTimeEquals(journalKey, anchorKey))
            throw Invalid();

        try
        {
            // Anchor should come from a separately controlled trust boundary.
            // Authenticating it before processing entries is intentional.
            using var checkpointDoc = JsonDocument.Parse(anchor.ToArray(),
                new JsonDocumentOptions { MaxDepth = 4 });
            var checkpoint = checkpointDoc.RootElement;
            RequireKeys(checkpoint, "schemaVersion", "count", "tipDigest", "mac");
            if (checkpoint.GetProperty("schemaVersion").GetInt32() != 1)
                throw Invalid();
            var expectedCount = checkpoint.GetProperty("count").GetInt32();
            var expectedTip = checkpoint.GetProperty("tipDigest").GetString()!;
            var checkpointTag = ReadTag(checkpoint.GetProperty("mac").GetString()!);
            if (expectedCount is < 1 or > MaxEntries)
                throw Invalid();
            _ = ReadTag(expectedTip);
            var checkpointPayload = AnchorPayload(expectedCount, expectedTip);
            var computedCheckpointTag = HMACSHA256.HashData(anchorKey, checkpointPayload);
            if (!CryptographicOperations.FixedTimeEquals(
                computedCheckpointTag, checkpointTag))
                throw Invalid();

            // Require strict UTF8, exact LF framing and bounded per-line size.
            // Empty lines and trailing garbage cannot be silently ignored.
            var lines = new UTF8Encoding(false, true).GetString(journal).Split('\n');
            if (lines.Length != expectedCount + 1 || lines[^1].Length != 0)
                throw Invalid();

            var previous = GenesisDigest;
            var eventIds = new HashSet<Guid>();
            for (var index = 0; index < expectedCount; index++)
            {
                var line = lines[index];
                if (line.Length == 0 || Encoding.UTF8.GetByteCount(line) > MaxEntryBytes)
                    throw Invalid();

                using var entryDoc = JsonDocument.Parse(line,
                    new JsonDocumentOptions { MaxDepth = 4 });
                var entry = entryDoc.RootElement;
                RequireKeys(entry, "schemaVersion", "sequence", "eventId",
                    "operation", "tenantId", "ownerSubject",
                    "previousDigest", "mac");
                var seq = entry.GetProperty("sequence").GetInt32();
                var eventId = entry.GetProperty("eventId").GetString()!;
                var operation = entry.GetProperty("operation").GetString()!;
                var tenantId = entry.GetProperty("tenantId").GetString()!;
                var owner = entry.GetProperty("ownerSubject").GetString()!;
                var chainLink = entry.GetProperty("previousDigest").GetString()!;
                var tag = ReadTag(entry.GetProperty("mac").GetString()!);

                if (entry.GetProperty("schemaVersion").GetInt32() != 1 ||
                    seq != index + 1 ||
                    !Guid.TryParseExact(eventId, "N", out var parsed) ||
                    !eventIds.Add(parsed) ||
                    operation != "erase-actor" ||
                    !ValidActor(tenantId, 128) || !ValidActor(owner, 255) ||
                    !string.Equals(chainLink, previous, StringComparison.Ordinal))
                    throw Invalid();

                var payload = EntryPayload(seq, eventId, operation,
                    tenantId, owner, chainLink);
                var expectedTag = HMACSHA256.HashData(journalKey, payload);
                if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
                    throw Invalid();

                // Previous-tag + canonical payload bind order AND content.
                // A later checkpoint, not the chain alone, detects truncation.
                previous = Convert.ToBase64String(
                    SHA256.HashData([.. payload, .. tag]));
            }

            if (!string.Equals(previous, expectedTip, StringComparison.Ordinal))
                throw Invalid();
            return new Verification(expectedCount, previous);
        }
        catch (Exception exception) when (exception is JsonException or
                   InvalidOperationException or FormatException or
                   DecoderFallbackException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static bool ValidActor(string? value, int maxLength) =>
        value is { Length: > 0 } &&
        value.Length <= maxLength &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(c => !char.IsControl(c));

    private static byte[] ReadTag(string encoded)
    {
        if (encoded.Length != 44)
            throw Invalid();
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length != 32 ||
            !string.Equals(Convert.ToBase64String(raw), encoded,
                StringComparison.Ordinal))
            throw Invalid();
        return raw;
    }

    private static void RequireKeys(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != names.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !actual.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw Invalid();
    }

    private static InvalidDataException Invalid() =>
        new("RECOVERY_JOURNAL_INVALID: external journal/checkpoint verification failed.");
}
