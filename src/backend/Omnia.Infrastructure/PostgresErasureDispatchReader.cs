using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// Erasure metadata fetched through a bounded, role-gated SQL function.
/// Contains personal identifiers: never write this record to logs or CI
/// artifacts. Its timestamp is NOT a reliable commit-order watermark.
/// </summary>
public sealed record ErasureDispatchEvent(
    Guid Id,
    string TenantId,
    string OwnerSubject,
    string EventType,
    DateTimeOffset OccurredAt);

/// <summary>
/// Read-only dispatch primitive for a separately provisioned PostgreSQL login.
/// Does not ACK, delete, mutate, or sign events. Persistent publication must
/// be idempotent and independently witnessed before a DB cursor may advance.
/// </summary>
public sealed class PostgresErasureDispatchReader(NpgsqlDataSource dispatchDb)
{
    public const int MaxBatchSize = 100;

    public Task<IReadOnlyList<ErasureDispatchEvent>> ReadBatchAsync(
        int batchSize, DateTimeOffset? afterCreatedAt, Guid? afterId,
        CancellationToken ct)
    {
        if (batchSize is < 1 or > MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (afterCreatedAt.HasValue != afterId.HasValue)
            throw new ArgumentException("Both dispatch cursor values are required together.");
        return ReadCoreAsync(batchSize, afterCreatedAt, afterId, ct);
    }

    private async Task<IReadOnlyList<ErasureDispatchEvent>> ReadCoreAsync(
        int batchSize, DateTimeOffset? afterCreatedAt, Guid? afterId,
        CancellationToken ct)
    {
        await using var cmd = dispatchDb.CreateCommand("""
            SELECT event_id, tenant_id, owner_sub, event_type, occurred_at
              FROM public.omnia_read_erasure_outbox(
                @batch_size, @after_time, @after_id)
            """);
        cmd.Parameters.AddWithValue("batch_size", batchSize);
        cmd.Parameters.Add(new NpgsqlParameter("after_time", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)afterCreatedAt ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid)
        {
            Value = (object?)afterId ?? DBNull.Value
        });

        var result = new List<ErasureDispatchEvent>(batchSize);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (result.Count >= batchSize)
                throw new InvalidOperationException(
                    "The dispatch SQL routine exceeded its required bound.");
            result.Add(new ErasureDispatchEvent(
                rows.GetGuid(0), rows.GetString(1), rows.GetString(2),
                rows.GetString(3), rows.GetFieldValue<DateTimeOffset>(4)));
        }
        return result;
    }

    /// <summary>
    /// Require an EXECUTE-only metadata role: no ownership, BYPASSRLS,
    /// direct table permissions, audit or retention function privileges.
    /// Does not treat a superuser or API runtime role as a dispatcher.
    /// </summary>
    public static async Task VerifyRoleAsync(NpgsqlDataSource dispatchDb,
        CancellationToken ct = default)
    {
        await using var verify = dispatchDb.CreateCommand("""
            SELECT NOT (
                r.rolsuper OR r.rolbypassrls
                OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid = 'public.erasure_outbox'::regclass), 'MEMBER')
                OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid = 'public.memory_entries'::regclass), 'MEMBER')
                OR has_table_privilege(r.oid, 'public.erasure_outbox',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'public.memory_entries',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'public.consent_grants',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_function_privilege(r.oid,
                    'public.omnia_count_legacy_memories()'::regprocedure, 'EXECUTE')
                OR has_function_privilege(r.oid,
                    'public.omnia_purge_expired_memories(integer)'::regprocedure, 'EXECUTE')
            )
            AND has_function_privilege(r.oid,
                'public.omnia_read_erasure_outbox(integer,timestamptz,uuid)'::regprocedure,
                'EXECUTE')
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        if (await verify.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException(
                "Erasure dispatch requires a distinct EXECUTE-only no-bypass role.");
    }
}
