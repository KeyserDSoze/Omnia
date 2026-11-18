using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Offline backfill needs SELECT/UPDATE only, never API's INSERT/DELETE
/// rights or the count-only audit role. This augments FORCE RLS verification.
/// </summary>
public static class PostgresBackfillRoleGuard
{
    public static async Task VerifyAsync(NpgsqlDataSource db,
        CancellationToken ct = default)
    {
        await PostgresRuntimeGuard.VerifyAsync(db, ct);
        await using var command = db.CreateCommand("""
            SELECT
                has_table_privilege(current_user, 'public.memory_entries', 'SELECT')
                AND has_table_privilege(current_user, 'public.memory_entries', 'UPDATE')
                AND NOT has_table_privilege(current_user, 'public.memory_entries',
                    'INSERT, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                AND NOT has_table_privilege(current_user, 'public.consent_grants',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                AND NOT has_function_privilege(current_user,
                    'public.omnia_count_legacy_memories()'::regprocedure, 'EXECUTE')
            """);
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException(
                "Offline backfill requires a distinct SELECT/UPDATE-only data role.");
    }
}
