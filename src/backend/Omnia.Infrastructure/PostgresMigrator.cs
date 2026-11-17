using System.Reflection;
using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Schema owner/migration credentials only, never the runtime API connection.
/// </summary>
public sealed class PostgresMigrator(NpgsqlDataSource db, string? runtimeRole = null)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(728185);", conn, tx))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var schema = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS omnia_schema_migrations
              (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            """, conn, tx))
            await schema.ExecuteNonQueryAsync(ct);
        var scripts = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
        foreach (var script in scripts)
        {
            var version = script.Split('.')[^2];
            await using var exists = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM omnia_schema_migrations WHERE version = @version)", conn, tx);
            exists.Parameters.AddWithValue("version", version);
            if ((bool)(await exists.ExecuteScalarAsync(ct) ?? false)) continue;
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(script)
                ?? throw new InvalidOperationException($"Missing migration: {script}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            await using (var apply = new NpgsqlCommand(sql, conn, tx))
                await apply.ExecuteNonQueryAsync(ct);
            await using var record = new NpgsqlCommand(
                "INSERT INTO omnia_schema_migrations(version) VALUES (@version)", conn, tx);
            record.Parameters.AddWithValue("version", version);
            await record.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(runtimeRole))
        {
            // The role must be provisioned by the database administrator. Never
            // create it implicitly with a default/static credential.
            await using var lookup = new NpgsqlCommand("""
                SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = @role
                """, conn, tx);
            lookup.Parameters.AddWithValue("role", runtimeRole);
            await using (var reader = await lookup.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) ||
                    reader.GetBoolean(0) || reader.GetBoolean(1))
                    throw new InvalidOperationException(
                        "Runtime DB role is missing or has SUPERUSER/BYPASSRLS.");
            }
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRole);
            await using var grant = new NpgsqlCommand($"""
                GRANT USAGE ON SCHEMA public TO {quoted};
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON TABLE public.memory_entries, public.consent_grants TO {quoted};
                """, conn, tx);
            await grant.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
