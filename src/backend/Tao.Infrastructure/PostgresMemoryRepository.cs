using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

public sealed class PostgresMemoryRepository(NpgsqlDataSource db, IEnvelopeEncryption crypto)
    : IMemoryRepository, IMemoryPageRepository
{
    public async Task<MemoryEntry> UpsertAsync(MemoryEntry entry, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db,
            entry.TenantId, entry.OwnerSubject, ct);
        var conn = scope.Connection;
        var tx = scope.Transaction;
        await PostgresActorLock.AcquireAsync(conn, tx,
            entry.TenantId, entry.OwnerSubject, ct);
        await using (var gate = new NpgsqlCommand("""
            SELECT id FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @subject
                AND source = @source AND purpose = 'memory.capture'
                -- granted_at comes from the API clock: tolerate small API/DB skew.
                AND revoked_at IS NULL AND granted_at <= now() + interval '2 minutes'
                AND expires_at > now()
            LIMIT 1 FOR SHARE
            """, conn, tx))
        {
            gate.Parameters.AddWithValue("tenant", entry.TenantId);
            gate.Parameters.AddWithValue("subject", entry.OwnerSubject);
            gate.Parameters.AddWithValue("source", entry.Source);
            if (await gate.ExecuteScalarAsync(ct) is not Guid)
                throw new ConsentRequiredException();
        }

        // Canonical identity is (tenant, owner, source, externalId). For a
        // still-live key, an identical request is a read-only retry, NEVER an
        // UPDATE with a new AES-GCM DEK, timestamp or embedding.
        // A changed body is an explicit conflict rather than a silent rewrite.
        // Actor lock + FOR UPDATE are held in the SAME scoped RLS transaction.
        var canonicalId = entry.Id;
        var unchangedLiveRetry = false;
        await using (var existing = new NpgsqlCommand("""
            SELECT id, expires_at, content_type, occurred_at, sealed_payload
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @subject
              AND source = @source AND external_id = @external
            FOR UPDATE
            """, conn, tx))
        {
            existing.Parameters.AddWithValue("tenant", entry.TenantId);
            existing.Parameters.AddWithValue("subject", entry.OwnerSubject);
            existing.Parameters.AddWithValue("source", entry.Source);
            existing.Parameters.AddWithValue("external", entry.ExternalId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                canonicalId = reader.GetGuid(0);
                var expiresAt = reader.GetFieldValue<DateTimeOffset>(1);
                if (expiresAt > DateTimeOffset.UtcNow)
                {
                    // Compare the original protected fields, NOT the random
                    // ciphertext (random DEKs differ even for equal plaintext).
                    var previous = SealedMemoryPayload.Decrypt(crypto,
                        reader.GetFieldValue<byte[]>(4),
                        entry.TenantId, entry.OwnerSubject, canonicalId);

                    // PostgreSQL timestamptz has microsecond precision;
                    // Npgsql/DateTimeOffset may carry 100ns ticks before insert.
                    // Distinguish a genuine changed event time, not its lost
                    // sub-microsecond precision on normal retries.
                    var sameTime = reader.GetFieldValue<DateTimeOffset>(3)
                        .ToUniversalTime().Ticks / 10 ==
                        entry.OccurredAt.ToUniversalTime().Ticks / 10;
                    if (!sameTime ||
                        !string.Equals(reader.GetString(2), entry.ContentType,
                            StringComparison.Ordinal) ||
                        !string.Equals(previous.RawContent, entry.RawContent,
                            StringComparison.Ordinal) ||
                        !string.Equals(previous.NormalizedJson, entry.NormalizedJson,
                            StringComparison.Ordinal) ||
                        !string.Equals(previous.Summary, entry.Summary,
                            StringComparison.Ordinal) ||
                        !previous.Tags.SequenceEqual(entry.Tags, StringComparer.Ordinal))
                        throw new MemoryIdempotencyConflictException();

                    unchangedLiveRetry = true;
                }
                // An already-expired key can be explicitly recaptured, but
                // only after the same active-consent check above.
            }
        }

        if (unchangedLiveRetry)
        {
            await scope.CommitAsync(ct);
            return entry.WithPersistedId(canonicalId);
        }

        var sealedPayload = SealedMemoryPayload.Encrypt(crypto, entry, canonicalId);

        // Both the active-grant row lock and the memory write live in ONE transaction.
        // A concurrent revoke cannot commit between authorization and persistence.
        await using var command = new NpgsqlCommand("""
            INSERT INTO memory_entries
              (id, tenant_id, owner_sub, source, external_id, content_type,
               occurred_at, embedding, embedding_model, sealed_payload)
            VALUES (@id, @tenant, @subject, @source, @external, @mime, @occurred,
               CAST(@embedding AS vector), @model, @sealed)
            ON CONFLICT (tenant_id, owner_sub, source, external_id)
            DO UPDATE SET sealed_payload = EXCLUDED.sealed_payload,
              content_type = EXCLUDED.content_type, occurred_at = EXCLUDED.occurred_at,
              embedding = EXCLUDED.embedding, embedding_model = EXCLUDED.embedding_model,
              -- Only expired rows may be reacquired. Live duplicates are
              -- handled read-only above; conflicting input fails closed.
              expires_at = now() + interval '30 days'
            WHERE memory_entries.expires_at <= now()
            RETURNING id
            """, conn, tx);
        command.Parameters.AddWithValue("id", canonicalId);
        command.Parameters.AddWithValue("tenant", entry.TenantId);
        command.Parameters.AddWithValue("subject", entry.OwnerSubject);
        command.Parameters.AddWithValue("source", entry.Source);
        command.Parameters.AddWithValue("external", entry.ExternalId);
        command.Parameters.AddWithValue("mime", entry.ContentType);
        command.Parameters.AddWithValue("occurred", entry.OccurredAt);
        command.Parameters.AddWithValue("sealed", sealedPayload);
        command.Parameters.AddWithValue("embedding", entry.Embedding is null
            ? DBNull.Value : (object)("[" + string.Join(",", entry.Embedding.Select(
                n => n.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"));
        command.Parameters.AddWithValue("model", (object?)entry.EmbeddingModel ?? DBNull.Value);
        // If a different concurrent writer inserted/renewed this key,
        // never overwrite its live row or expose its contents.
        var id = await command.ExecuteScalarAsync(ct) is Guid persistedId
            ? persistedId : throw new MemoryIdempotencyConflictException();
        await scope.CommitAsync(ct);
        return entry.WithPersistedId(id);
    }

    public async Task<IReadOnlyList<MemorySummary>> RecentAsync(
        string tenantId, string ownerSubject, int limit, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, source, occurred_at, embedding IS NOT NULL, sealed_payload
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @subject
              AND expires_at > now()
            ORDER BY occurred_at DESC, id DESC LIMIT @limit
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("subject", ownerSubject);
        command.Parameters.AddWithValue("limit", limit);
        var output = new List<MemorySummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            output.Add(ReadSummary(reader, tenantId, ownerSubject));
        return output;
    }

    public async Task<MemoryPage> PageAsync(
        string tenantId, string ownerSubject, int limit, MemoryPageCursor? before,
        CancellationToken ct)
    {
        // Application validates 1..50, but guard direct repository consumers.
        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var scope = await PostgresActorScope.OpenAsync(
            db, tenantId, ownerSubject, ct);
        // Fixed, parameterized query variants preserve (occurred_at DESC, id DESC)
        // and never trust a cursor to choose owner, tenant, schema or SQL.
        var sql = before is null ? """
            SELECT id, source, occurred_at, embedding IS NOT NULL, sealed_payload
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @subject
              AND expires_at > now()
            ORDER BY occurred_at DESC, id DESC LIMIT @limit
            """ : """
            SELECT id, source, occurred_at, embedding IS NOT NULL, sealed_payload
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @subject
              AND expires_at > now()
              AND (occurred_at, id) < (@beforeAt, @beforeId)
            ORDER BY occurred_at DESC, id DESC LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(
            sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("subject", ownerSubject);
        command.Parameters.AddWithValue("limit", limit + 1);
        if (before is not null)
        {
            command.Parameters.AddWithValue("beforeAt", before.OccurredAt);
            command.Parameters.AddWithValue("beforeId", before.Id);
        }

        var result = new List<MemorySummary>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadSummary(reader, tenantId, ownerSubject));

        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        var last = result.Count == 0 ? null : result[^1];
        return new MemoryPage(result, hasMore && last is not null
            ? new MemoryPageCursor(last.OccurredAt, last.Id).Encode()
            : null);
    }

    private MemorySummary ReadSummary(
        NpgsqlDataReader reader, string tenantId, string ownerSubject)
    {
        var id = reader.GetGuid(0);
        // Fail closed: a row that cannot be authenticated is never shown.
        var content = SealedMemoryPayload.Decrypt(crypto,
            reader.GetFieldValue<byte[]>(4), tenantId, ownerSubject, id);
        return new MemorySummary(id, reader.GetString(1), content.Summary, content.Tags,
            reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3));
    }
}
