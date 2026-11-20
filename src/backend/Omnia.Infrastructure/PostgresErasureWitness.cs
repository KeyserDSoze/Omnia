using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// A single receipt persisted by a separate witness PostgreSQL cluster.
/// Sequence is monotonically allocated by a locked row in the WITNESS
/// cluster; it says NOTHING about source transaction order/completeness.
/// </summary>
public sealed record ErasureWitnessReceipt(
    long Sequence, Guid EventId, string TenantId,
    string OwnerSubject, string EventType);

/// <summary>
/// CI-only independently provisioned PostgreSQL witness; never registered
/// in the production API. Writer role receives EXECUTE on ONE bounded
/// SECURITY DEFINER append function and NO raw table privileges.
/// </summary>
public sealed class PostgresErasureWitness(NpgsqlDataSource witness)
{
    public async Task<long> AppendAsync(ErasureDispatchEvent entry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var command = witness.CreateCommand("""
            SELECT witness.append_erasure(@id, @tenant, @owner, @operation)
            """);
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("tenant", entry.TenantId);
        command.Parameters.AddWithValue("owner", entry.OwnerSubject);
        command.Parameters.AddWithValue("operation", entry.EventType);
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Witness event not acknowledged."));
    }

    public static async Task VerifyWriterAsync(
        NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        await WitnessRoleGuard.VerifyAsync(dataSource,
            wantWriter: true, ct);
}

/// <summary>
/// Read-only access to witness receipts for controlled DR inspection.
/// A separate reader identity is required; no direct SELECT permission.
/// </summary>
public sealed class PostgresErasureWitnessReader(NpgsqlDataSource witness)
{
    public async Task<ErasureWitnessReceipt?> FindAsync(
        Guid eventId, CancellationToken ct)
    {
        await using var cmd = witness.CreateCommand("""
            SELECT sequence, event_id, tenant_id, owner_sub, event_type
              FROM witness.find_erasure(@id)
            """);
        cmd.Parameters.AddWithValue("id", eventId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        return new ErasureWitnessReceipt(
            rows.GetInt64(0), rows.GetGuid(1), rows.GetString(2),
            rows.GetString(3), rows.GetString(4));
    }

    public async Task<IReadOnlyList<ErasureWitnessReceipt>> ReadBatchAsync(
        long afterSequence, int limit, CancellationToken ct)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var cmd = witness.CreateCommand("""
            SELECT sequence, event_id, tenant_id, owner_sub, event_type
              FROM witness.list_erasures(@seq, @limit)
            """);
        cmd.Parameters.AddWithValue("seq", afterSequence);
        cmd.Parameters.AddWithValue("limit", limit);
        var records = new List<ErasureWitnessReceipt>();
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
            records.Add(new ErasureWitnessReceipt(
                rows.GetInt64(0), rows.GetGuid(1), rows.GetString(2),
                rows.GetString(3), rows.GetString(4)));
        return records;
    }

    public static async Task VerifyReaderAsync(
        NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        await WitnessRoleGuard.VerifyAsync(dataSource,
            wantWriter: false, ct);
}

internal static class WitnessRoleGuard
{
    internal static async Task VerifyAsync(NpgsqlDataSource witness,
        bool wantWriter, CancellationToken ct)
    {
        // Only the intended EXECUTE capability is permitted. Reject owner,
        // admin, BYPASSRLS and any direct witness table access.
        await using var cmd = witness.CreateCommand("""
            SELECT NOT (
                r.rolsuper OR r.rolbypassrls
                OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid = 'witness.erasure_events'::regclass), 'MEMBER')
                OR has_table_privilege(r.oid, 'witness.erasure_events',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'witness.journal_head',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            )
            AND has_function_privilege(r.oid,
                'witness.append_erasure(uuid,varchar,varchar,varchar)'::regprocedure,
                'EXECUTE') = @writer
            AND has_function_privilege(r.oid,
                'witness.find_erasure(uuid)'::regprocedure, 'EXECUTE') <> @writer
            AND has_function_privilege(r.oid,
                'witness.list_erasures(bigint,integer)'::regprocedure,
                'EXECUTE') <> @writer
              FROM pg_roles r WHERE r.rolname = current_user
            """);
        cmd.Parameters.AddWithValue("writer", wantWriter);
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException(
                "Witness identity must be independent and EXECUTE-only.");
    }
}

/// <summary>
/// Test-only bridge from committed outbox to a separate PostgreSQL
/// failure domain. No persisted source cursor (created_at is not commit
/// order), no source ACK and no background/autonomous delivery guarantee.
/// </summary>
public sealed class PostgresErasureOutboxWitnessPublisher(
    NpgsqlDataSource dispatchDb, NpgsqlDataSource witnessDb)
{
    public async Task<int> PublishBatchAsync(int maxEvents,
        CancellationToken ct)
    {
        if (maxEvents is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        await PostgresErasureDispatchReader.VerifyRoleAsync(dispatchDb, ct);
        await PostgresErasureWitness.VerifyWriterAsync(witnessDb, ct);
        var reader = new PostgresErasureDispatchReader(dispatchDb);
        var sink = new PostgresErasureWitness(witnessDb);
        DateTimeOffset? afterAt = null;
        Guid? afterId = null;
        var count = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= maxEvents)
            {
                // Never claim a complete scan when bounded to a truncated
                // prefix of source data. No advancement is persisted.
                if ((await reader.ReadBatchAsync(
                    1, afterAt, afterId, ct)).Count > 0)
                    throw new InvalidOperationException(
                        "Source outbox exceeds the per-run witness bound.");
                return count;
            }
            var batch = await reader.ReadBatchAsync(
                Math.Min(100, maxEvents - count), afterAt, afterId, ct);
            if (batch.Count == 0) return count;
            foreach (var entry in batch)
            {
                if (entry.EventType != "erase-actor")
                    throw new InvalidOperationException(
                        "Unexpected event type from source outbox.");
                await sink.AppendAsync(entry, ct);
                count++;
            }
            afterAt = batch[^1].OccurredAt;
            afterId = batch[^1].Id;
        }
    }
}
