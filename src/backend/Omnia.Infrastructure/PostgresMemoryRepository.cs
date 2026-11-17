using Npgsql;
using Omnia.Application;
using Omnia.Domain;

namespace Omnia.Infrastructure;

public sealed class PostgresMemoryRepository(NpgsqlDataSource db) : IMemoryRepository
{
    public async Task<MemoryEntry> UpsertAsync(MemoryEntry entry, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            INSERT INTO memory_entries
              (id, tenant_id, owner_sub, source, external_id, content_type, raw_content,
               normalized_json, summary, tags, occurred_at, embedding, embedding_model)
            VALUES (@id, @tenant, @subject, @source, @external, @mime, @raw,
               CAST(@normalized AS jsonb), @summary, @tags, @occurred,
               CAST(@embedding AS vector), @model)
            ON CONFLICT (tenant_id, owner_sub, source, external_id)
            DO UPDATE SET raw_content = EXCLUDED.raw_content,
              normalized_json = EXCLUDED.normalized_json,
              summary = EXCLUDED.summary,
              tags = EXCLUDED.tags, occurred_at = EXCLUDED.occurred_at,
              embedding = EXCLUDED.embedding, embedding_model = EXCLUDED.embedding_model
            RETURNING id
            """);
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("tenant", entry.TenantId);
        command.Parameters.AddWithValue("subject", entry.OwnerSubject);
        command.Parameters.AddWithValue("source", entry.Source);
        command.Parameters.AddWithValue("external", entry.ExternalId);
        command.Parameters.AddWithValue("mime", entry.ContentType);
        command.Parameters.AddWithValue("raw", entry.RawContent);
        command.Parameters.AddWithValue("normalized", entry.NormalizedJson);
        command.Parameters.AddWithValue("summary", entry.Summary);
        command.Parameters.AddWithValue("tags", entry.Tags.ToArray());
        command.Parameters.AddWithValue("occurred", entry.OccurredAt);
        command.Parameters.AddWithValue("embedding", (object?) (entry.Embedding is null
            ? DBNull.Value : "[" + string.Join(",", entry.Embedding.Select(
                n => n.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"));
        command.Parameters.AddWithValue("model", (object?)entry.EmbeddingModel ?? DBNull.Value);
        var id = (Guid)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Insert did not return a record ID."));
        return entry.WithPersistedId(id);
    }

    public async Task<IReadOnlyList<MemorySummary>> RecentAsync(
        string tenantId, string ownerSubject, int limit, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT id, source, summary, tags, occurred_at, embedding IS NOT NULL
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @subject
            ORDER BY occurred_at DESC, id DESC LIMIT @limit
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("subject", ownerSubject);
        command.Parameters.AddWithValue("limit", limit);
        var output = new List<MemorySummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            output.Add(new MemorySummary(reader.GetGuid(0), reader.GetString(1),
                reader.GetString(2), reader.GetFieldValue<string[]>(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5)));
        return output;
    }
}
