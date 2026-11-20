using System.Security.Cryptography;
using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// Domain-separated authenticated prefix of remote PRE-ERASE intentions.
/// It does not certify a DELETE transaction, outbox COMMIT or user consent.
/// Only count, prefix digest and HMAC leave the second witness PGDATA.
/// </summary>
public static class IntentCheckpoint
{
    public const int MaxIntents = 10000;
    public const int MaxCheckpointBytes = 4096;
    private const string Purpose = "pre-erase-intent-prefix";
    private static readonly string Genesis = Convert.ToBase64String(
        SHA256.HashData("omnia-preerase-intent-checkpoint-v1/genesis"u8));

    public static byte[] Create(IReadOnlyList<PreEraseIntentReceipt> intents,
        ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw Invalid();
        var digest = CalculateTip(intents, intents.Count);
        var message = Payload(intents.Count, digest);
        var mac = Convert.ToBase64String(HMACSHA256.HashData(key, message));
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = Purpose,
            count = intents.Count,
            tipDigest = digest,
            mac
        });
    }

    /// <summary>
    /// Check only a previously anchored prefix. Later intents may exist but
    /// are NOT attested until the independent anchor advances.
    /// A rolled-back witness missing an anchored intent is rejected.
    /// </summary>
    public static int Verify(IReadOnlyList<PreEraseIntentReceipt> intents,
        ReadOnlySpan<byte> checkpoint, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32 ||
            checkpoint.Length is < 2 or > MaxCheckpointBytes)
            throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(checkpoint.ToArray(),
                new JsonDocumentOptions { MaxDepth = 4 });
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = obj.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Length != 5 ||
                names.Distinct(StringComparer.Ordinal).Count() != 5 ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["schemaVersion", "purpose", "count", "tipDigest", "mac"]))
                throw Invalid();
            if (obj.GetProperty("schemaVersion").GetInt32() != 1 ||
                obj.GetProperty("purpose").GetString() != Purpose)
                throw Invalid();
            var count = obj.GetProperty("count").GetInt32();
            if (count is < 1 or > MaxIntents) throw Invalid();
            var tipRaw = CanonicalTag(obj.GetProperty("tipDigest").GetString());
            var macRaw = CanonicalTag(obj.GetProperty("mac").GetString());
            var tip = Convert.ToBase64String(tipRaw);
            var mac = Convert.ToBase64String(macRaw);
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, purpose = Purpose, count, tipDigest = tip, mac
            });
            if (!checkpoint.SequenceEqual(canonical)) throw Invalid();
            var computed = HMACSHA256.HashData(key, Payload(count, tip));
            if (!CryptographicOperations.FixedTimeEquals(computed, macRaw))
                throw Invalid();
            var observed = Convert.FromBase64String(CalculateTip(intents, count));
            if (!CryptographicOperations.FixedTimeEquals(observed, tipRaw))
                throw Invalid();
            return count;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or
                   InvalidOperationException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static byte[] Payload(int count, string digest) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = Purpose,
            count,
            tipDigest = digest
        });

    private static string CalculateTip(
        IReadOnlyList<PreEraseIntentReceipt> intents, int count)
    {
        if (count is < 1 or > MaxIntents ||
            intents.Count < count || intents.Count > MaxIntents)
            throw Invalid();
        var previous = Genesis;
        var seen = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var x = intents[i];
            if (x.Sequence != i + 1 || x.RequestId == Guid.Empty ||
                !seen.Add(x.RequestId) || x.IntentType != "erase-actor-intent" ||
                !ValidActor(x.TenantId, 128) ||
                !ValidActor(x.OwnerSubject, 255))
                throw Invalid();
            var record = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                purpose = Purpose,
                sequence = x.Sequence,
                requestId = x.RequestId.ToString("N"),
                tenantId = x.TenantId,
                ownerSubject = x.OwnerSubject,
                intentType = x.IntentType,
                previousDigest = previous
            });
            previous = Convert.ToBase64String(SHA256.HashData(record));
        }
        return previous;
    }

    private static bool ValidActor(string? value, int bound) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= bound &&
        value.All(c => !char.IsControl(c));

    private static byte[] CanonicalTag(string? encoded)
    {
        if (encoded is not { Length: 44 }) throw Invalid();
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length != 32 || Convert.ToBase64String(raw) != encoded)
            throw Invalid();
        return raw;
    }

    private static InvalidDataException Invalid() =>
        new("PREERASE_INTENT_CHECKPOINT_INVALID.");
}
