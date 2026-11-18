using System.Text.Json;
using Npgsql;
using Omnia.Application;

namespace Omnia.Infrastructure;

/// <summary>
/// RLS-scoped, streaming JSON export of current PostgreSQL personal data;
/// atomic, repeatable deletion of memory (including vectors) and consent rows.
/// </summary>
public sealed class PostgresPersonalDataService(NpgsqlDataSource db) : IPersonalDataService
{
    public async Task WriteExportAsync(string tenantId, string ownerSubject,
        Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("A writable export destination is required.", nameof(destination));
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(
            actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);

        await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("scope", "omnia-postgresql-primary");
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WriteString("tenantId", tenantId);
        writer.WriteString("subject", ownerSubject);
        writer.WritePropertyName("memoryEntries");
        writer.WriteStartArray();

        await using (var command = new NpgsqlCommand("""
            SELECT id, source, external_id, content_type, raw_content,
                   normalized_json::text, summary, tags, occurred_at,
                   received_at, embedding::text, embedding_model
            FROM memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @owner
            ORDER BY occurred_at, id
            """, actor.Connection, actor.Transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("owner", ownerSubject);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = 0;
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("id", reader.GetGuid(0));
                writer.WriteString("source", reader.GetString(1));
                writer.WriteString("externalId", reader.GetString(2));
                writer.WriteString("contentType", reader.GetString(3));
                writer.WriteString("rawContent", reader.GetString(4));
                writer.WritePropertyName("normalized");
                using (var normalized = JsonDocument.Parse(reader.GetString(5)))
                    normalized.RootElement.WriteTo(writer);
                writer.WriteString("summary", reader.GetString(6));
                writer.WritePropertyName("tags");
                writer.WriteStartArray();
                foreach (var tag in reader.GetFieldValue<string[]>(7))
                    writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteString("occurredAt", reader.GetFieldValue<DateTimeOffset>(8));
                writer.WriteString("receivedAt", reader.GetFieldValue<DateTimeOffset>(9));
                writer.WritePropertyName("embedding");
                if (reader.IsDBNull(10))
                    writer.WriteNullValue();
                else
                {
                    // pgvector's text representation is a valid JSON numeric array.
                    // Export derived data too; not just the note's summary.
                    using var vector = JsonDocument.Parse(reader.GetString(10));
                    vector.RootElement.WriteTo(writer);
                }
                if (reader.IsDBNull(11)) writer.WriteNull("embeddingModel");
                else writer.WriteString("embeddingModel", reader.GetString(11));
                writer.WriteEndObject();
                if (++rows % 25 == 0) await writer.FlushAsync(ct);
            }
        }

        writer.WriteEndArray();
        writer.WritePropertyName("consentGrants");
        writer.WriteStartArray();
        await using (var command = new NpgsqlCommand("""
            SELECT id, source, purpose, policy_version, granted_at, expires_at, revoked_at
            FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @owner
            ORDER BY granted_at, id
            """, actor.Connection, actor.Transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("owner", ownerSubject);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = 0;
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("id", reader.GetGuid(0));
                writer.WriteString("source", reader.GetString(1));
                writer.WriteString("purpose", reader.GetString(2));
                writer.WriteString("policyVersion", reader.GetString(3));
                writer.WriteString("grantedAt", reader.GetFieldValue<DateTimeOffset>(4));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(5));
                if (reader.IsDBNull(6)) writer.WriteNull("revokedAt");
                else writer.WriteString("revokedAt", reader.GetFieldValue<DateTimeOffset>(6));
                writer.WriteEndObject();
                if (++rows % 25 == 0) await writer.FlushAsync(ct);
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
        // COMMIT must not happen before the entire export was serialized.
        await actor.CommitAsync(ct);
    }

    public async Task<PersonalDataErasureResult> EraseAsync(string tenantId,
        string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(
            actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);

        // First remove authorization. Locking the actor also serializes grants
        // and captures; no older in-flight write can commit after this erasure.
        await using var consentDelete = new NpgsqlCommand("""
            DELETE FROM consent_grants WHERE tenant_id = @tenant AND owner_sub = @owner
            """, actor.Connection, actor.Transaction);
        consentDelete.Parameters.AddWithValue("tenant", tenantId);
        consentDelete.Parameters.AddWithValue("owner", ownerSubject);
        var consents = await consentDelete.ExecuteNonQueryAsync(ct);

        await using var memoryDelete = new NpgsqlCommand("""
            DELETE FROM memory_entries WHERE tenant_id = @tenant AND owner_sub = @owner
            """, actor.Connection, actor.Transaction);
        memoryDelete.Parameters.AddWithValue("tenant", tenantId);
        memoryDelete.Parameters.AddWithValue("owner", ownerSubject);
        var memories = await memoryDelete.ExecuteNonQueryAsync(ct);

        await actor.CommitAsync(ct);
        return new PersonalDataErasureResult(memories, consents, DateTimeOffset.UtcNow);
    }
}
