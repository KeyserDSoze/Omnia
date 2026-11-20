using System.Data;
using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Offline, read-only, snapshot-consistent scan of the separate CI witness.
/// Requires the dedicated EXECUTE-only reader role. Paging within one
/// REPEATABLE READ transaction prevents concurrent appends from moving the
/// snapshot between batches. No raw witness table permissions are granted.
/// </summary>
public static class PostgresErasureWitnessSnapshot
{
    public static async Task<IReadOnlyList<ErasureWitnessReceipt>> ReadAsync(
        NpgsqlDataSource witness, int maxReceipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(witness);
        if (maxReceipts is < 1 or > WitnessCheckpoint.MaxReceipts)
            throw new ArgumentOutOfRangeException(nameof(maxReceipts));
        await PostgresErasureWitnessReader.VerifyReaderAsync(witness, ct);
        await using var connection = await witness.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(ct);

        var events = new List<ErasureWitnessReceipt>();
        var ids = new HashSet<Guid>();
        long previous = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Probe one extra row when the bound is full to detect overflow.
            var take = Math.Min(100, Math.Max(1, maxReceipts - events.Count));
            var next = new List<ErasureWitnessReceipt>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT sequence, event_id, tenant_id, owner_sub, event_type
                  FROM witness.list_erasures(@after, @limit)
                """, connection, tx))
            {
                cmd.Parameters.AddWithValue("after", previous);
                cmd.Parameters.AddWithValue("limit", take);
                await using var rows = await cmd.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                    next.Add(new ErasureWitnessReceipt(
                        rows.GetInt64(0), rows.GetGuid(1),
                        rows.GetString(2), rows.GetString(3),
                        rows.GetString(4)));
            }

            if (events.Count + next.Count > maxReceipts)
                throw new InvalidDataException("WITNESS_SNAPSHOT_BOUND_EXCEEDED.");
            if (next.Count == 0) break;

            foreach (var record in next)
            {
                if (record.Sequence != previous + 1 ||
                    record.EventId == Guid.Empty || !ids.Add(record.EventId) ||
                    record.EventType != "erase-actor")
                    throw new InvalidDataException("WITNESS_SNAPSHOT_INCONSISTENT.");
                previous = record.Sequence;
                events.Add(record);
            }
            if (events.Count == maxReceipts)
            {
                // Next iteration executes a bounded one-row probe in the
                // SAME read-only snapshot; never silently truncate the audit.
                continue;
            }
        }
        await tx.CommitAsync(ct);
        return events;
    }
}
