using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Runtime role gate: no owner/BYPASSRLS/SUPERUSER, FORCE RLS on all personal-data
/// tables and no TRUNCATE (RLS does not filter TRUNCATE).
/// </summary>
public static class PostgresRuntimeGuard
{
    public static async Task VerifyAsync(NpgsqlDataSource runtime, CancellationToken ct = default)
    {
        await using var command = runtime.CreateCommand("""
            SELECT r.rolsuper, r.rolbypassrls,
              pg_has_role((SELECT oid FROM pg_roles WHERE rolname = current_user), (SELECT relowner FROM pg_class
                  WHERE oid = 'public.memory_entries'::regclass), 'MEMBER'),
              pg_has_role((SELECT oid FROM pg_roles WHERE rolname = current_user), (SELECT relowner FROM pg_class
                  WHERE oid = 'public.consent_grants'::regclass), 'MEMBER'),
              (SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity)
                  FROM pg_class c WHERE c.oid IN
                    ('public.memory_entries'::regclass, 'public.consent_grants'::regclass)
              ) AS rls_forced,
              has_table_privilege(current_user, 'public.memory_entries', 'TRUNCATE')
                OR has_table_privilege(current_user, 'public.consent_grants', 'TRUNCATE')
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(4) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.GetBoolean(2) || reader.GetBoolean(3) ||
            !reader.GetBoolean(4) || reader.GetBoolean(5))
            throw new InvalidOperationException(
                "The data role requires non-owner, non-superuser privileges without BYPASSRLS " +
                "or TRUNCATE, and FORCE RLS on all personal tables.");
    }
}
