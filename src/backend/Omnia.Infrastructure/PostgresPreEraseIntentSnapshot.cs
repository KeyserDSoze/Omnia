using System.Data;
using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// A single consistent read-only snapshot of all intents in the independent
/// witness PGDATA. The intent-reader role has no raw table privileges.
/// A bound overflow, gap or duplicate must never yield a valid prefix.
/// </summary>
public static class PostgresPreEraseIntentSnapshot
{
    public static async Task<IReadOnlyList<PreEraseIntentReceipt>> ReadAsync(
        NpgsqlDataSource intentReader, int maxIntents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intentReader);
        if (maxIntents is < 1 or > IntentCheckpoint.MaxIntents)
            throw new ArgumentOutOfRangeException(nameof(maxIntents));
        await PostgresPreEraseIntentReader.VerifyReaderAsync(intentReader, ct);
        await using var connection = await intentReader.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);

        var intents = new List<PreEraseIntentReceipt>();
        var seen = new HashSet<Guid>();
        long last = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // An additional one-row probe in the SAME transaction prevents
            // silently accepting a prefix cut at the maximum bound.
            var take = Math.Min(100, Math.Max(1, maxIntents - intents.Count));
            var page = new List<PreEraseIntentReceipt>();
            await using (var command = new NpgsqlCommand("""
                SELECT sequence, request_id, tenant_id, owner_sub, intent_type
                  FROM witness.list_erase_intents(@after, @limit)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("after", last);
                command.Parameters.AddWithValue("limit", take);
                await using var rows = await command.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                    page.Add(new PreEraseIntentReceipt(
                        rows.GetInt64(0), rows.GetGuid(1),
                        rows.GetString(2), rows.GetString(3),
                        rows.GetString(4)));
            }
            if (intents.Count + page.Count > maxIntents)
                throw new InvalidDataException("INTENT_SNAPSHOT_OVERFLOW.");
            if (page.Count == 0) break;
            foreach (var x in page)
            {
                if (x.Sequence != last + 1 || x.RequestId == Guid.Empty ||
                    !seen.Add(x.RequestId) || x.IntentType != "erase-actor-intent")
                    throw new InvalidDataException("INTENT_SNAPSHOT_INVALID.");
                last = x.Sequence;
                intents.Add(x);
            }
        }
        await transaction.CommitAsync(ct);
        return intents;
    }
}
