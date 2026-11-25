using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Narrowly privileged retention job. It must connect as a separate
/// non-owner, non-BYPASSRLS role with EXECUTE on one fixed SQL function
/// and NO direct access to memory/consent rows.
/// </summary>
public sealed class PostgresRetentionRepository(NpgsqlDataSource workerDb)
{
    public async Task<int> PurgeBatchAsync(int batchSize, CancellationToken ct) =>
        await PurgeAsync("SELECT public.tao_purge_expired_memories(@batch)", batchSize, ct);

    /// <summary>Expired triage decisions, pending OAuth authorizations and action audit rows.</summary>
    public async Task<int> PurgeWorkStateBatchAsync(int batchSize, CancellationToken ct) =>
        await PurgeAsync("SELECT public.tao_purge_expired_work_state(@batch)", batchSize, ct);

    private async Task<int> PurgeAsync(string sql, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var command = workerDb.CreateCommand(sql);
        command.Parameters.AddWithValue("batch", batchSize);
        return (int)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Retention purge returned no result."));
    }

    public static async Task VerifyWorkerRoleAsync(NpgsqlDataSource workerDb,
        CancellationToken ct = default)
    {
        await using var command = workerDb.CreateCommand("""
            SELECT r.rolsuper, r.rolbypassrls,
                   bool_or(has_table_privilege(r.oid, c.oid, 'SELECT')
                        OR has_table_privilege(r.oid, c.oid, 'INSERT')
                        OR has_table_privilege(r.oid, c.oid, 'UPDATE')
                        OR has_table_privilege(r.oid, c.oid, 'DELETE')),
                   bool_or(pg_has_role(r.oid, c.relowner, 'MEMBER')),
                   has_function_privilege(r.oid,
                       'public.tao_purge_expired_memories(integer)', 'EXECUTE'),
                   has_function_privilege(r.oid,
                       'public.tao_purge_expired_work_state(integer)', 'EXECUTE'),
                   count(c.oid)
            FROM pg_roles r
            CROSS JOIN pg_class c
            WHERE r.rolname = current_user
              AND c.relnamespace = 'public'::regnamespace AND c.relname = ANY(@tables)
            GROUP BY r.oid, r.rolsuper, r.rolbypassrls
            """);
        command.Parameters.AddWithValue("tables", PostgresRuntimeGuard.PersonalTables.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.GetBoolean(2) || reader.GetBoolean(3) ||
            !reader.GetBoolean(4) || !reader.GetBoolean(5) ||
            reader.GetInt64(6) != PostgresRuntimeGuard.PersonalTables.Count)
            throw new InvalidOperationException(
                "Retention login must have EXECUTE on the bounded purge functions " +
                "but no personal-table access, ownership, SUPERUSER or BYPASSRLS.");
    }
}
