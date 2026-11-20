using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// A LIVE-source read-only check that committed erasure events have durable
/// matching witness receipts. This does NOT prove source completeness
/// after primary PITR: erased source rows can be irretrievably absent.
/// No cursor/ACK is stored and no recovery-ready signal is issued.
/// </summary>
public sealed class PostgresErasureLiveDeliveryAudit(
    NpgsqlDataSource sourceDispatch, NpgsqlDataSource witnessReader)
{
    public const int MaxOutboxEvents = 1000;

    /// <summary>
    /// Verify a known UUID from the committed source outbox against the
    /// independent witness. Missing or mismatched receipt fails closed.
    /// Returns only the remote witness sequence, not PII.
    /// </summary>
    public async Task<long> VerifyKnownEventAsync(Guid eventId, CancellationToken ct)
    {
        if (eventId == Guid.Empty) throw Invalid();
        await VerifyRolesAsync(ct);
        var source = await ReadSourceSnapshotAsync(ct);
        var entry = source.SingleOrDefault(x => x.Id == eventId)
            ?? throw Invalid();
        var acknowledged = await new PostgresErasureWitnessReader(witnessReader)
            .FindAsync(eventId, ct) ?? throw Invalid();
        return RequireMatchingReceipt(entry, acknowledged);
    }

    /// <summary>
    /// Live bounded health gate. Verifies EVERY event present in a
    /// consistent primary snapshot; the witness can accept events meanwhile.
    /// It cannot detect erasures absent from the PRIMARY after disaster
    /// and MUST NOT be used as sole recovery release authorization.
    /// </summary>
    public async Task<int> VerifyAllAsync(CancellationToken ct)
    {
        await VerifyRolesAsync(ct);
        var snapshot = await ReadSourceSnapshotAsync(ct);
        var reader = new PostgresErasureWitnessReader(witnessReader);
        foreach (var item in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var witness = await reader.FindAsync(item.Id, ct) ?? throw Invalid();
            RequireMatchingReceipt(item, witness);
        }
        return snapshot.Count;
    }

    /// <summary>
    /// Validate UUID plus immutable tenant/actor/event type scope. This
    /// function must also reject a positive-sequence forged receipt.
    /// </summary>
    public static long RequireMatchingReceipt(
        ErasureDispatchEvent source, ErasureWitnessReceipt witnessed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(witnessed);
        if (source.Id == Guid.Empty || source.Id != witnessed.EventId ||
            witnessed.Sequence <= 0 ||
            source.EventType != "erase-actor" ||
            witnessed.EventType != source.EventType ||
            !ValidActor(source.TenantId, 128) ||
            !ValidActor(source.OwnerSubject, 255) ||
            !string.Equals(source.TenantId, witnessed.TenantId,
                StringComparison.Ordinal) ||
            !string.Equals(source.OwnerSubject, witnessed.OwnerSubject,
                StringComparison.Ordinal))
            throw Invalid();
        return witnessed.Sequence;
    }

    private async Task VerifyRolesAsync(CancellationToken ct)
    {
        // Verify both restricted identities on each invocation, never
        // accidentally accepting app/owner/writer connections.
        await PostgresErasureDispatchReader.VerifyRoleAsync(sourceDispatch, ct);
        await PostgresErasureWitnessReader.VerifyReaderAsync(witnessReader, ct);
    }

    private async Task<IReadOnlyList<ErasureDispatchEvent>> ReadSourceSnapshotAsync(
        CancellationToken ct)
    {
        await using var connection = await sourceDispatch.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(ct);

        var output = new List<ErasureDispatchEvent>();
        var seen = new HashSet<Guid>();
        DateTimeOffset? afterAt = null;
        Guid? afterId = null;
        DateTimeOffset? previousAt = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // At the bound, perform one more bounded probe in the SAME
            // snapshot rather than claiming a truncated prefix is complete.
            var take = Math.Min(PostgresErasureDispatchReader.MaxBatchSize,
                Math.Max(1, MaxOutboxEvents - output.Count));
            var page = new List<ErasureDispatchEvent>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT event_id, tenant_id, owner_sub, event_type, occurred_at
                  FROM public.omnia_read_erasure_outbox(
                    @batch_size, @after_time, @after_id)
                """, connection, tx))
            {
                cmd.Parameters.AddWithValue("batch_size", take);
                cmd.Parameters.Add(new NpgsqlParameter(
                    "after_time", NpgsqlDbType.TimestampTz)
                {
                    Value = (object?)afterAt ?? DBNull.Value
                });
                cmd.Parameters.Add(new NpgsqlParameter(
                    "after_id", NpgsqlDbType.Uuid)
                {
                    Value = (object?)afterId ?? DBNull.Value
                });
                await using var rows = await cmd.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                    page.Add(new ErasureDispatchEvent(rows.GetGuid(0),
                        rows.GetString(1), rows.GetString(2),
                        rows.GetString(3),
                        rows.GetFieldValue<DateTimeOffset>(4)));
            }
            if (page.Count == 0) break;
            if (output.Count + page.Count > MaxOutboxEvents)
                throw Invalid();

            foreach (var entry in page)
            {
                if (entry.Id == Guid.Empty || !seen.Add(entry.Id) ||
                    entry.EventType != "erase-actor" ||
                    !ValidActor(entry.TenantId, 128) ||
                    !ValidActor(entry.OwnerSubject, 255) ||
                    (previousAt.HasValue && entry.OccurredAt < previousAt.Value))
                    throw Invalid();
                output.Add(entry);
                previousAt = entry.OccurredAt;
            }
            afterAt = page[^1].OccurredAt;
            afterId = page[^1].Id;
        }
        await tx.CommitAsync(ct);
        return output;
    }

    private static bool ValidActor(string? value, int max) =>
        value is { Length: > 0 } && value.Length <= max &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(x => !char.IsControl(x));

    private static InvalidDataException Invalid() =>
        new("ERASURE_DELIVERY_UNCONFIRMED: committed source erasure has no valid witness receipt.");
}
