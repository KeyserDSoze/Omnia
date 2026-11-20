using System.Security.Cryptography;
using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// Authenticated digest of a CONTIGUOUS PREFIX of the independent witness.
/// The checkpoint must be held outside witness PGDATA, with a separately
/// protected HMAC key, to detect witness rollback. A mutable local copy
/// or a checkpoint re-issued after rollback is NOT an anti-rollback authority.
/// No PII is serialized into the checkpoint, only sequence/count/digest/MAC.
/// </summary>
public static class WitnessCheckpoint
{
    public const int MaxReceipts = 10000;
    public const int MaxCheckpointBytes = 4096;
    private static readonly string Genesis = Convert.ToBase64String(
        SHA256.HashData("omnia-witness-checkpoint-v1/genesis"u8));

    public static byte[] Create(IReadOnlyList<ErasureWitnessReceipt> receipts,
        ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw Invalid();
        var tip = CalculateTip(receipts, receipts.Count);
        var payload = CheckpointPayload(receipts.Count, tip);
        var mac = Convert.ToBase64String(HMACSHA256.HashData(key, payload));
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            count = receipts.Count,
            tipDigest = tip,
            mac
        });
    }

    /// <summary>
    /// Verifies the anchored PREFIX, not new events committed after the
    /// checkpoint. A higher sequence may exist but is not independently
    /// attested until an external authority preserves a newer checkpoint.
    /// Does not grant permission to release recovery quarantine.
    /// </summary>
    public static int Verify(IReadOnlyList<ErasureWitnessReceipt> receipts,
        ReadOnlySpan<byte> checkpoint, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32 || checkpoint.Length is < 2 or > MaxCheckpointBytes)
            throw Invalid();

        try
        {
            using var doc = JsonDocument.Parse(checkpoint.ToArray(),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid();
            var names = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Length != 4 ||
                names.Distinct(StringComparer.Ordinal).Count() != 4 ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["schemaVersion", "count", "tipDigest", "mac"]))
                throw Invalid();

            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw Invalid();
            var count = root.GetProperty("count").GetInt32();
            if (count is < 1 or > MaxReceipts)
                throw Invalid();
            var expectedTip = ReadCanonicalTag(
                root.GetProperty("tipDigest").GetString());
            var suppliedMac = ReadCanonicalTag(root.GetProperty("mac").GetString());
            var tipText = Convert.ToBase64String(expectedTip);
            var macText = Convert.ToBase64String(suppliedMac);
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, count, tipDigest = tipText, mac = macText
            });
            // Require exact canonical JSON bytes: reject trailing whitespace,
            // alternate representations, duplicate properties and junk.
            if (!checkpoint.SequenceEqual(canonical))
                throw Invalid();
            var computedMac = HMACSHA256.HashData(
                key, CheckpointPayload(count, tipText));
            if (!CryptographicOperations.FixedTimeEquals(
                computedMac, suppliedMac))
                throw Invalid();

            var actualTip = Convert.FromBase64String(CalculateTip(receipts, count));
            if (!CryptographicOperations.FixedTimeEquals(
                actualTip, expectedTip))
                throw Invalid();
            return count;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or
                   InvalidOperationException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static byte[] CheckpointPayload(int count, string tip) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, count, tipDigest = tip
        });

    private static string CalculateTip(
        IReadOnlyList<ErasureWitnessReceipt> receipts, int count)
    {
        if (count is < 1 or > MaxReceipts || receipts.Count < count ||
            receipts.Count > MaxReceipts)
            throw Invalid();

        var previous = Genesis;
        var seen = new HashSet<Guid>();
        for (var index = 0; index < count; index++)
        {
            var row = receipts[index];
            if (row.Sequence != index + 1 || row.EventId == Guid.Empty ||
                !seen.Add(row.EventId) || row.EventType != "erase-actor" ||
                !ValidActor(row.TenantId, 128) ||
                !ValidActor(row.OwnerSubject, 255))
                throw Invalid();

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                sequence = row.Sequence,
                eventId = row.EventId.ToString("N"),
                tenantId = row.TenantId,
                ownerSubject = row.OwnerSubject,
                eventType = row.EventType,
                previousDigest = previous
            });
            previous = Convert.ToBase64String(SHA256.HashData(payload));
        }
        return previous;
    }

    private static bool ValidActor(string? actor, int maximum) =>
        actor is { Length: > 0 } && actor.Length <= maximum &&
        !string.IsNullOrWhiteSpace(actor) &&
        actor.All(c => !char.IsControl(c));

    private static byte[] ReadCanonicalTag(string? encoded)
    {
        if (encoded is not { Length: 44 }) throw Invalid();
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length != 32 || Convert.ToBase64String(raw) != encoded)
            throw Invalid();
        return raw;
    }

    private static InvalidDataException Invalid() =>
        new("WITNESS_CHECKPOINT_INVALID: anchored witness prefix could not be verified.");
}
