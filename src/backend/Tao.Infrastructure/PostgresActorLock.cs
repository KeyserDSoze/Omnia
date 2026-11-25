using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Actor-wide transaction lock shared by capture, grant, export and erasure.
/// Erasure deletes consents before memories, while all writes obey this lock,
/// preventing in-flight ingestion from recreating data after committed erasure.
/// This does not prevent the user from explicitly re-granting access afterwards.
/// </summary>
public static class PostgresActorLock
{
    public static async Task AcquireAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string tenantId, string ownerSubject,
        CancellationToken ct)
    {
        // Length framing avoids ambiguous actor pairs. A rare hash collision
        // only serializes unrelated actors; it never grants cross-actor access.
        var actor = $"{tenantId.Length}:{tenantId}{ownerSubject.Length}:{ownerSubject}";
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@actor, 0))",
            connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }
}
