using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Shared role gate for API, legacy-backfill and count-only audit identities:
/// no owner/BYPASSRLS and FORCE RLS on all personal-data tables.
/// API-specific INSERT privilege is checked separately by VerifyApiAsync.
/// </summary>
public static class PostgresRuntimeGuard
{
    /// <summary>
    /// API needs column-scoped INSERT for erasure event emission. Offline
    /// backfill and count-only audit MUST NOT be granted that permission.
    /// </summary>
    public static async Task VerifyApiAsync(NpgsqlDataSource runtime,
        CancellationToken ct = default)
    {
        await VerifyAsync(runtime, ct);
        await using var privilege = runtime.CreateCommand("""
            SELECT has_column_privilege(current_user, 'public.erasure_outbox', 'id', 'INSERT')
               AND has_column_privilege(current_user, 'public.erasure_outbox', 'tenant_id', 'INSERT')
               AND has_column_privilege(current_user, 'public.erasure_outbox', 'owner_sub', 'INSERT')
               AND has_column_privilege(current_user, 'public.erasure_outbox', 'event_type', 'INSERT')
               AND NOT has_column_privilege(current_user,
                   'public.erasure_outbox', 'created_at', 'INSERT')
            """);
        if (await privilege.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException(
                "API runtime requires narrow outbox INSERT but may not override event timestamps.");
    }

    public static async Task VerifyAsync(NpgsqlDataSource runtime, CancellationToken ct = default)
    {
        await using var command = runtime.CreateCommand("""
            SELECT r.rolsuper, r.rolbypassrls,
              pg_has_role((SELECT oid FROM pg_roles WHERE rolname = current_user), (SELECT relowner FROM pg_class
                  WHERE oid = 'public.memory_entries'::regclass), 'MEMBER'),
              pg_has_role((SELECT oid FROM pg_roles WHERE rolname = current_user), (SELECT relowner FROM pg_class
                  WHERE oid = 'public.consent_grants'::regclass), 'MEMBER'),
              pg_has_role((SELECT oid FROM pg_roles WHERE rolname = current_user), (SELECT relowner FROM pg_class
                  WHERE oid = 'public.erasure_outbox'::regclass), 'MEMBER'),
              (SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity)
                  FROM pg_class c WHERE c.oid IN
                    ('public.memory_entries'::regclass, 'public.consent_grants'::regclass,
                     'public.erasure_outbox'::regclass)
              ) AS rls_forced,
              has_column_privilege(current_user, 'public.erasure_outbox', 'id', 'INSERT'),
              has_table_privilege(current_user, 'public.erasure_outbox',
                  'SELECT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(5) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
            !reader.GetBoolean(5) || reader.GetBoolean(7))
            throw new InvalidOperationException(
                "The data role requires non-owner, non-superuser privileges without BYPASSRLS, " +
                "FORCE RLS on all personal tables and no outbox read/modify permissions.");
    }
}
