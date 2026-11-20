using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// Third-PGDATA, CI-ONLY one-shot PRE-source-DELETE attempt claim.
/// A claim does NOT mean that the source DELETE was executed or committed.
/// It can intentionally strand an uncertain request and require operator
/// investigation rather than permit a dangerous retry after primary PITR.
/// </summary>
public sealed class PostgresPreEraseExecutionClaimWriter(NpgsqlDataSource thirdCluster)
{
    public async Task ClaimOnceAsync(Guid requestId, string tenantId,
        string ownerSubject, byte[] signingKey, CancellationToken ct)
    {
        var scopeHmac = PreEraseExecutionClaimScope.Compute(
            requestId, tenantId, ownerSubject, signingKey);
        try
        {
            await PreEraseExecutionClaimRoleGuard.VerifyAsync(
                thirdCluster, writer: true, ct);
            await using var command = thirdCluster.CreateCommand("""
                SELECT admission.claim_once(@id, @scope)
                """);
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.Add("scope", NpgsqlDbType.Bytea).Value = scopeHmac;
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new InvalidDataException("EXECUTION_CLAIM_NOT_ACKNOWLEDGED.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scopeHmac);
        }
        // Do NOT retry on timeout or ambiguous commit; the claim may have
        // persisted remotely even if the client did not receive the ACK.

    }

    public static Task VerifyWriterAsync(NpgsqlDataSource connection,
        CancellationToken ct = default) =>
        PreEraseExecutionClaimRoleGuard.VerifyAsync(
            connection, writer: true, ct);

}

internal static class PreEraseExecutionClaimScope
{
    internal static byte[] Compute(Guid requestId, string tenantId,
        string ownerSubject, byte[] signingKey)
    {
        if (requestId == Guid.Empty ||
            !ValidScope(tenantId, 128) || !ValidScope(ownerSubject, 255))
            throw new ArgumentException("Invalid synthetic claim scope.");
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length != 32)
            throw new InvalidDataException("EXECUTION_CLAIM_SIGNING_KEY_INVALID.");
        // One canonical payload for both the insert and independent reader:
        // no owner or tenant plaintext is persisted on THIRD PGDATA.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = "omnia-preerase-single-use-claim-v1",
            requestId = requestId.ToString("N"),
            tenantId,
            ownerSubject
        });
        try
        {
            return HMACSHA256.HashData(signingKey, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static bool ValidScope(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value.All(c => !char.IsControl(c));
}

/// <summary>
/// Restricted, read-only, evidence-of-ATTEMPT lookup, not source commit proof.
/// </summary>
public sealed class PostgresPreEraseExecutionClaimReader(NpgsqlDataSource thirdCluster)
{
    public async Task<bool> HasClaimAsync(Guid requestId, CancellationToken ct)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("Invalid claim UUID.", nameof(requestId));
        await PreEraseExecutionClaimRoleGuard.VerifyAsync(
            thirdCluster, writer: false, ct);
        await using var command = thirdCluster.CreateCommand("""
            SELECT admission.has_claim(@id)
            """);
        command.Parameters.AddWithValue("id", requestId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// Authenticates a consumed attempt's exact request, tenant and owner.
    /// Unlike HasClaimAsync(UUID), this rejects a foreign/tampered scope
    /// while using only the restricted EXECUTE-only third-cluster reader.
    /// This proves attempted admission, NEVER source DELETE commit.
    /// </summary>
    public async Task VerifyClaimAsync(Guid requestId, string tenantId,
        string ownerSubject, byte[] signingKey, CancellationToken ct)
    {
        var expected = PreEraseExecutionClaimScope.Compute(
            requestId, tenantId, ownerSubject, signingKey);
        try
        {
            await PreEraseExecutionClaimRoleGuard.VerifyAsync(
                thirdCluster, writer: false, ct);
            await using var command = thirdCluster.CreateCommand("""
                SELECT admission.claim_scope(@id)
                """);
            command.Parameters.AddWithValue("id", requestId);
            var observed = await command.ExecuteScalarAsync(ct) as byte[];
            if (observed is null)
                throw new InvalidDataException("EXECUTION_CLAIM_MISSING.");
            try
            {
                if (observed.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(expected, observed))
                    throw new InvalidDataException("EXECUTION_CLAIM_SCOPE_INVALID.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static Task VerifyReaderAsync(NpgsqlDataSource connection,
        CancellationToken ct = default) =>
        PreEraseExecutionClaimRoleGuard.VerifyAsync(
            connection, writer: false, ct);
}

internal static class PreEraseExecutionClaimRoleGuard
{
    internal static async Task VerifyAsync(NpgsqlDataSource connection,
        bool writer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var expected = writer ? "omnia_claim_writer" : "omnia_claim_reader";
        await using (var identity = connection.CreateCommand("SELECT current_user"))
        {
            if (!string.Equals(
                await identity.ExecuteScalarAsync(ct) as string, expected,
                StringComparison.Ordinal))
                throw new InvalidOperationException("EXECUTION_CLAIM_ROLE_DENIED.");
        }

        await using var guard = connection.CreateCommand("""
            SELECT NOT (
                r.rolsuper OR r.rolbypassrls OR r.rolcreaterole
                OR r.rolcreatedb OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid = 'admission.execution_claims'::regclass),
                    'MEMBER')
                OR has_table_privilege(r.oid, 'admission.execution_claims',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            )
            AND has_function_privilege(r.oid,
                'admission.claim_once(uuid,bytea)'::regprocedure, 'EXECUTE')
                = @writer
            AND has_function_privilege(r.oid,
                'admission.has_claim(uuid)'::regprocedure, 'EXECUTE')
                <> @writer
            AND has_function_privilege(r.oid,
                'admission.claim_scope(uuid)'::regprocedure, 'EXECUTE')
                <> @writer
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        guard.Parameters.AddWithValue("writer", writer);
        if (await guard.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("EXECUTION_CLAIM_ROLE_DENIED.");
    }
}
