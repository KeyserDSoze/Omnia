using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// The third-PGDATA request UUID alone cannot attest which synthetic
/// tenant/actor was admitted. Validate the stored scope MAC read-only.
/// No claim result certifies the source DELETE or authorizes recovery.
/// </summary>
public sealed class PreEraseClaimScopeVerificationTests
{
    [Fact]
    public async Task AuthenticatedThirdClusterClaimIsActorBoundAndTamperEvident()
    {
        await using var writerDb = NpgsqlDataSource.Create(Required(
            "OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB"));
        await using var readerDb = NpgsqlDataSource.Create(Required(
            "OMNIA_TEST_EXECUTION_CLAIM_READER_DB"));
        await using var adminDb = NpgsqlDataSource.Create(Required(
            "OMNIA_TEST_EXECUTION_CLAIM_ADMIN_DB"));
        var writer = new PostgresPreEraseExecutionClaimWriter(writerDb);
        var reader = new PostgresPreEraseExecutionClaimReader(readerDb);
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        const string tenant = "synthetic-claim-scope-tenant";
        const string subject = "synthetic-claim-scope-owner";

        // No matching attempt has been admitted to the third cluster.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant, subject, key, default));
        await writer.ClaimOnceAsync(id, tenant, subject, key, default);
        Assert.True(await reader.HasClaimAsync(id, default));

        await reader.VerifyClaimAsync(id, tenant, subject, key, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant + "-foreign", subject, key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant, subject + "-foreign", key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant, subject,
                RandomNumberGenerator.GetBytes(32), default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(otherId, tenant, subject, key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant, subject, new byte[31], default));

        // Claim writer must not gain reader powers, and neither SQL
        // capability may SELECT/UPDATE raw attempt rows.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseExecutionClaimReader(writerDb)
                .VerifyClaimAsync(id, tenant, subject, key, default));
        await using (var forbidden = writerDb.CreateCommand(
            "SELECT admission.claim_scope(@id)"))
        {
            forbidden.Parameters.AddWithValue("id", id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                forbidden.ExecuteScalarAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        // Tamper with only this synthetic claim using a privileged TEST
        // role. Its UUID still exists; a boolean HasClaim cannot detect
        // this, but authenticated scope verification MUST fail closed.
        await using (var tamper = adminDb.CreateCommand("""
            UPDATE admission.execution_claims
               SET scope_hmac = @tampered WHERE request_id = @id
            """))
        {
            tamper.Parameters.AddWithValue("id", id);
            tamper.Parameters.AddWithValue("tampered",
                RandomNumberGenerator.GetBytes(32));
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }
        Assert.True(await reader.HasClaimAsync(id, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.VerifyClaimAsync(id, tenant, subject, key, default));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException(
                "Synthetic third-cluster PostgreSQL connection unavailable.");
}
