using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Runtime role gate: no owner/BYPASSRLS/SUPERUSER, FORCE RLS on all personal-data
/// tables and no TRUNCATE (RLS does not filter TRUNCATE).
/// </summary>
public static class PostgresRuntimeGuard
{
    /// <summary>Every actor-scoped table that holds personal data or derived state.</summary>
    public static readonly IReadOnlyList<string> PersonalTables =
    [
        "memory_entries", "consent_grants", "connector_authorizations", "connector_accounts",
        "triage_decisions", "connector_actions", "user_settings"
    ];

    public static async Task VerifyAsync(NpgsqlDataSource runtime, CancellationToken ct = default)
    {
        await using var command = runtime.CreateCommand("""
            SELECT r.rolsuper, r.rolbypassrls,
              bool_or(pg_has_role(r.oid, c.relowner, 'MEMBER')),
              bool_and(c.relrowsecurity AND c.relforcerowsecurity),
              bool_or(has_table_privilege(r.oid, c.oid, 'TRUNCATE')),
              count(c.oid)
            FROM pg_roles r
            CROSS JOIN pg_class c
            WHERE r.rolname = current_user
              AND c.relnamespace = 'public'::regnamespace AND c.relname = ANY(@tables)
            GROUP BY r.rolsuper, r.rolbypassrls
            """);
        command.Parameters.AddWithValue("tables", PersonalTables.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) ||
            !reader.GetBoolean(3) || reader.GetBoolean(4) ||
            reader.GetInt64(5) != PersonalTables.Count)
            throw new InvalidOperationException(
                "The data role requires non-owner, non-superuser privileges without BYPASSRLS " +
                "or TRUNCATE, and FORCE RLS on all personal tables.");
    }
}
