using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Schema owner/migration credentials only, never the runtime API connection.
/// </summary>
public sealed class PostgresMigrator(NpgsqlDataSource db, string? runtimeRole = null, string? retentionRole = null)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(728185);", conn, tx))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var schema = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS tao_schema_migrations
              (version text PRIMARY KEY, checksum text NOT NULL,
               applied_at timestamptz NOT NULL DEFAULT now());
            """, conn, tx))
            await schema.ExecuteNonQueryAsync(ct);
        var scripts = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
        foreach (var script in scripts)
        {
            var version = script.Split('.')[^2];
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(script)
                ?? throw new InvalidOperationException($"Missing migration: {script}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            // Normalize line endings so Windows and Linux checkouts hash identically.
            var checksum = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
            await using var exists = new NpgsqlCommand(
                "SELECT checksum FROM tao_schema_migrations WHERE version = @version", conn, tx);
            exists.Parameters.AddWithValue("version", version);
            if (await exists.ExecuteScalarAsync(ct) is string applied)
            {
                if (!string.Equals(applied, checksum, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Migration {version} changed after it was applied; add a new migration instead.");
                continue;
            }
            await using (var apply = new NpgsqlCommand(sql, conn, tx))
                await apply.ExecuteNonQueryAsync(ct);
            await using var record = new NpgsqlCommand(
                "INSERT INTO tao_schema_migrations(version, checksum) VALUES (@version, @checksum)",
                conn, tx);
            record.Parameters.AddWithValue("version", version);
            record.Parameters.AddWithValue("checksum", checksum);
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
        if (!string.IsNullOrWhiteSpace(retentionRole))
        {
            if (string.Equals(retentionRole, runtimeRole, StringComparison.Ordinal))
                throw new InvalidOperationException("Retention and API roles must be distinct.");
            await using var lookup = new NpgsqlCommand("""
                SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = @role
                """, conn, tx);
            lookup.Parameters.AddWithValue("role", retentionRole);
            await using (var reader = await lookup.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) ||
                    reader.GetBoolean(0) || reader.GetBoolean(1))
                    throw new InvalidOperationException(
                        "Retention role is missing or has SUPERUSER/BYPASSRLS.");
            }
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(retentionRole);
            await using var grant = new NpgsqlCommand($"""
                GRANT USAGE ON SCHEMA public TO {quoted};
                GRANT EXECUTE ON FUNCTION public.tao_purge_expired_memories(integer)
                    TO {quoted};
                """, conn, tx);
            await grant.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
