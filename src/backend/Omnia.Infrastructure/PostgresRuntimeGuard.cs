using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Production startup gate: the API identity must not be able to bypass,
/// own, or disable row-level security for personal tables.
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
              ) AS rls_forced
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(4) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.GetBoolean(2) || reader.GetBoolean(3) || !reader.GetBoolean(4))
            throw new InvalidOperationException(
                "The API requires a non-owner, non-superuser PostgreSQL role without BYPASSRLS, " +
                "and FORCE RLS must be enabled on both personal tables.");
    }
}
