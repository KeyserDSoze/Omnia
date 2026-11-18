using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Narrowly privileged retention job. It must connect as a separate
/// non-owner, non-BYPASSRLS role with EXECUTE on one fixed SQL function
/// and NO direct access to memory/consent rows.
/// </summary>
public sealed class PostgresRetentionRepository(NpgsqlDataSource workerDb)
{
    public async Task<int> PurgeBatchAsync(int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var command = workerDb.CreateCommand(
            "SELECT public.omnia_purge_expired_memories(@batch)");
        command.Parameters.AddWithValue("batch", batchSize);
        return (int)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Retention purge returned no result."));
    }

    public static async Task VerifyWorkerRoleAsync(NpgsqlDataSource workerDb,
        CancellationToken ct = default)
    {
        await using var command = workerDb.CreateCommand("""
            SELECT r.rolsuper, r.rolbypassrls,
                   has_table_privilege(current_user::text, 'public.memory_entries', 'SELECT'),
                   has_table_privilege(current_user::text, 'public.memory_entries', 'DELETE'),
                   has_table_privilege(current_user::text, 'public.consent_grants', 'SELECT'),
                   has_function_privilege(current_user::text,
                       'public.omnia_purge_expired_memories(integer)', 'EXECUTE'),
                   pg_has_role(
                       (SELECT oid FROM pg_roles WHERE rolname = current_user),
                       (SELECT relowner FROM pg_class
                        WHERE oid = 'public.memory_entries'::regclass), 'MEMBER')
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
            !reader.GetBoolean(5) || reader.GetBoolean(6))
            throw new InvalidOperationException(
                "Retention login must have EXECUTE on the bounded purge function " +
                "but no personal-table access, ownership, SUPERUSER or BYPASSRLS.");
    }
}
