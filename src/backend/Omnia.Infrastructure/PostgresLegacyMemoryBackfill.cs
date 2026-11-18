using Npgsql;
using Omnia.Application;

namespace Omnia.Infrastructure;

/// <summary>
/// Offline, explicitly actor-scoped legacy encryption. Never registered in HTTP DI.
/// Requires a separate non-bypass DB identity and external key ring.
/// </summary>
public sealed class PostgresLegacyMemoryBackfill(NpgsqlDataSource db, IEnvelopeEncryption crypto)
{
    public async Task<int> BackfillBatchAsync(
        string tenantId, string ownerSubject, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be 1..500.");

        // Match capture and erasure lock order: actor FIRST, then row locks.
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(
            actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);

        var rows = new List<(Guid Id, SealedMemoryPayload.Content Content)>();
        await using (var find = new NpgsqlCommand("""
            SELECT id, raw_content, normalized_json::text, summary, tags
            FROM public.memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND sealed_payload IS NULL
            ORDER BY id
            LIMIT @batch FOR UPDATE SKIP LOCKED
            """, actor.Connection, actor.Transaction))
        {
            find.Parameters.AddWithValue("tenant", tenantId);
            find.Parameters.AddWithValue("owner", ownerSubject);
            find.Parameters.AddWithValue("batch", batchSize);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetGuid(0), new SealedMemoryPayload.Content(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<string[]>(4))));
        }

        foreach (var row in rows)
        {
            // Existing row UUID is the authenticated associated-data record ID.
            var ciphertext = SealedMemoryPayload.Encrypt(
                crypto, row.Content, tenantId, ownerSubject, row.Id);
            await using var update = new NpgsqlCommand("""
                UPDATE public.memory_entries
                SET sealed_payload = @sealed, raw_content = '[sealed]',
                    normalized_json = '{}'::jsonb, summary = '[sealed]',
                    tags = ARRAY[]::text[]
                WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
                    AND sealed_payload IS NULL
                """, actor.Connection, actor.Transaction);
            update.Parameters.AddWithValue("sealed", ciphertext);
            update.Parameters.AddWithValue("id", row.Id);
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("owner", ownerSubject);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Backfill row changed unexpectedly.");
        }

        // Failure rolls back the entire batch. Never renew TTL or reinsert.
        await actor.CommitAsync(ct);
        return rows.Count;
    }

    public async Task<long> CountLegacyAsync(
        string tenantId, string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM public.memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND sealed_payload IS NULL
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Legacy count unavailable."));
    }
}
