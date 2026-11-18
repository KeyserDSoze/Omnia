using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Global count-only integrity signal. This is NOT an actor-scoped API service.
/// Call exclusively with a dedicated audit login that cannot read personal
/// tables directly. The SECURITY DEFINER function exposes only a bigint.
/// </summary>
public sealed class PostgresLegacyBackfillAudit(NpgsqlDataSource auditDb)
{
    public async Task<long> CountRemainingAsync(CancellationToken ct)
    {
        // Reuse the runtime guard's non-superuser/non-owner/FORCE RLS checks.
        await PostgresRuntimeGuard.VerifyAsync(auditDb, ct);

        await using (var privileges = auditDb.CreateCommand("""
            SELECT has_table_privilege(current_user, 'public.memory_entries',
                       'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(current_user, 'public.consent_grants',
                       'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            """))
        {
            if ((bool)(await privileges.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Audit role check unavailable.")))
                throw new InvalidOperationException(
                    "Audit identity must have no direct access to personal tables.");
        }

        await using var count = auditDb.CreateCommand(
            "SELECT public.omnia_count_legacy_memories()");
        return (long)(await count.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Legacy count unavailable."));
    }
}
