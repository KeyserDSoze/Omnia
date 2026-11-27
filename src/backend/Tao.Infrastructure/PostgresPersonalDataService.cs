using System.Text.Json;
using Npgsql;
using Tao.Application;

namespace Tao.Infrastructure;

/// <summary>
/// RLS-scoped, streaming JSON export of current PostgreSQL personal data;
/// atomic, repeatable deletion of memory (including vectors) and consent rows.
/// </summary>
public sealed class PostgresPersonalDataService(NpgsqlDataSource db, IEnvelopeEncryption crypto) : IPersonalDataService
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
        writer.WriteString("scope", "tao-postgresql-primary");
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WriteString("tenantId", tenantId);
        writer.WriteString("subject", ownerSubject);
        writer.WritePropertyName("memoryEntries");
        writer.WriteStartArray();

        await using (var command = new NpgsqlCommand("""
            SELECT id, source, external_id, content_type, sealed_payload, occurred_at,
                   received_at, expires_at, embedding::text, embedding_model
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
                var content = SealedMemoryPayload.Decrypt(crypto, reader.GetFieldValue<byte[]>(4),
                    tenantId, ownerSubject, reader.GetGuid(0));
                writer.WriteString("storageProtection", "sealed-v1");
                writer.WriteString("rawContent", content.RawContent);
                writer.WritePropertyName("normalized");
                using (var normalized = JsonDocument.Parse(content.NormalizedJson))
                    normalized.RootElement.WriteTo(writer);
                writer.WriteString("summary", content.Summary);
                writer.WritePropertyName("tags");
                writer.WriteStartArray();
                foreach (var tag in content.Tags)
                    writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteString("occurredAt", reader.GetFieldValue<DateTimeOffset>(5));
                writer.WriteString("receivedAt", reader.GetFieldValue<DateTimeOffset>(6));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(7));
                writer.WritePropertyName("embedding");
                if (reader.IsDBNull(8))
                    writer.WriteNullValue();
                else
                {
                    // pgvector's text representation is a valid JSON numeric array.
                    using var vector = JsonDocument.Parse(reader.GetString(8));
                    vector.RootElement.WriteTo(writer);
                }
                if (reader.IsDBNull(9)) writer.WriteNull("embeddingModel");
                else writer.WriteString("embeddingModel", reader.GetString(9));
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
        await WriteWorkStateAsync(writer, actor, tenantId, ownerSubject, ct);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
        // COMMIT must not happen before the entire export was serialized.
        await actor.CommitAsync(ct);
    }

    /// <summary>Connected accounts (identity and scopes, never tokens), decisions and audit.</summary>
    private async Task WriteWorkStateAsync(Utf8JsonWriter writer, PostgresActorScope actor,
        string tenantId, string ownerSubject, CancellationToken ct)
    {
        writer.WritePropertyName("settings");
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT sealed_settings FROM user_settings WHERE tenant_id = @tenant AND owner_sub = @owner
            """))
        {
            if (await command.ExecuteScalarAsync(ct) is byte[] envelope)
                JsonSerializer.Serialize(writer,
                    PostgresSettingsStore.Open(crypto, envelope, tenantId, ownerSubject), PostgresSettingsStore.Json);
            else
                writer.WriteNullValue();
        }

        writer.WritePropertyName("connectorAccounts");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT id, provider, sealed_credentials, connected_at, updated_at FROM connector_accounts
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY connected_at, id
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var plain = crypto.Open(reader.GetFieldValue<byte[]>(2),
                    PostgresConnectorStore.CredentialScope(tenantId, ownerSubject, id));
                try
                {
                    using var credentials = JsonDocument.Parse(plain);
                    var identity = credentials.RootElement.GetProperty("Identity");
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("provider", reader.GetString(1));
                    writer.WriteString("externalAccountId", identity.GetProperty("ExternalId").GetString());
                    writer.WriteString("displayName", identity.GetProperty("DisplayName").GetString());
                    writer.WritePropertyName("email");
                    identity.GetProperty("Email").WriteTo(writer);
                    writer.WritePropertyName("scopes");
                    credentials.RootElement.GetProperty("Tokens").GetProperty("Scopes").WriteTo(writer);
                    writer.WriteString("tokens", "sealed-not-exported");
                    writer.WriteString("connectedAt", reader.GetFieldValue<DateTimeOffset>(3));
                    writer.WriteString("updatedAt", reader.GetFieldValue<DateTimeOffset>(4));
                    writer.WriteEndObject();
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("pendingConnectorAuthorizations");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT provider, expires_at FROM connector_authorizations
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY expires_at
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("provider", reader.GetString(0));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(1));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("triageDecisions");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT item_key, decision, decided_at, snoozed_until, applied_to_source, expires_at
            FROM triage_decisions
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY decided_at, item_key
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            var rows = 0;
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("itemKey", reader.GetString(0));
                writer.WriteString("decision", reader.GetString(1));
                writer.WriteString("decidedAt", reader.GetFieldValue<DateTimeOffset>(2));
                if (reader.IsDBNull(3)) writer.WriteNull("snoozedUntil");
                else writer.WriteString("snoozedUntil", reader.GetFieldValue<DateTimeOffset>(3));
                writer.WriteBoolean("appliedToSource", reader.GetBoolean(4));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(5));
                writer.WriteEndObject();
                if (++rows % 50 == 0) await writer.FlushAsync(ct);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("connectorActions");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT id, account_id, action, outcome, requested_at FROM connector_actions
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY requested_at, id
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            var rows = 0;
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("id", reader.GetGuid(0));
                writer.WriteString("accountId", reader.GetGuid(1));
                writer.WriteString("action", reader.GetString(2));
                writer.WriteString("outcome", reader.GetString(3));
                writer.WriteString("requestedAt", reader.GetFieldValue<DateTimeOffset>(4));
                writer.WriteEndObject();
                if (++rows % 50 == 0) await writer.FlushAsync(ct);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("assistantUsage");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT day, calls, tokens, expires_at FROM assistant_usage
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY day
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("day", reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd"));
                writer.WriteNumber("calls", reader.GetInt32(1));
                writer.WriteNumber("tokens", reader.GetInt64(2));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(3));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("reminders");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT id, sealed_text, repeat, at_time, on_date, time_zone, enabled, next_run_at, last_sent_at, created_at
            FROM reminders WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY created_at, id
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var plain = crypto.Open(reader.GetFieldValue<byte[]>(1), PostgresReminderStore.Scope(tenantId, ownerSubject, id));
                try
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("text", System.Text.Encoding.UTF8.GetString(plain));
                    writer.WriteString("repeat", reader.GetString(2));
                    writer.WriteString("time", reader.GetFieldValue<TimeOnly>(3).ToString("HH:mm"));
                    if (reader.IsDBNull(4)) writer.WriteNull("date");
                    else writer.WriteString("date", reader.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd"));
                    writer.WriteString("timeZone", reader.GetString(5));
                    writer.WriteBoolean("enabled", reader.GetBoolean(6));
                    if (reader.IsDBNull(7)) writer.WriteNull("nextRunAt");
                    else writer.WriteString("nextRunAt", reader.GetFieldValue<DateTimeOffset>(7));
                    if (reader.IsDBNull(8)) writer.WriteNull("lastSentAt");
                    else writer.WriteString("lastSentAt", reader.GetFieldValue<DateTimeOffset>(8));
                    writer.WriteString("createdAt", reader.GetFieldValue<DateTimeOffset>(9));
                    writer.WriteEndObject();
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            }
        }
        writer.WriteEndArray();

        // The address of a subscription is a capability: only its host and the dates are exported, never the keys.
        writer.WritePropertyName("pushSubscriptions");
        writer.WriteStartArray();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT id, sealed_subscription, created_at, last_success_at, failures FROM push_subscriptions
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY created_at, id
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var plain = crypto.Open(reader.GetFieldValue<byte[]>(1), PostgresPushSubscriptionStore.Scope(tenantId, ownerSubject, id));
                try
                {
                    using var sealedDocument = JsonDocument.Parse(plain);
                    var host = new Uri(sealedDocument.RootElement.GetProperty("Endpoint").GetString()!).Host;
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("service", host);
                    writer.WriteString("keys", "sealed-not-exported");
                    writer.WriteString("createdAt", reader.GetFieldValue<DateTimeOffset>(2));
                    if (reader.IsDBNull(3)) writer.WriteNull("lastSuccessAt");
                    else writer.WriteString("lastSuccessAt", reader.GetFieldValue<DateTimeOffset>(3));
                    writer.WriteNumber("failures", reader.GetInt32(4));
                    writer.WriteEndObject();
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            }
        }
        writer.WriteEndArray();
    }

    private static NpgsqlCommand Command(PostgresActorScope actor, string tenantId, string ownerSubject,
        string sql)
    {
        var command = new NpgsqlCommand(sql, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        return command;
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

        int accounts;
        await using (var accountDelete = Command(actor, tenantId, ownerSubject,
            "DELETE FROM connector_accounts WHERE tenant_id = @tenant AND owner_sub = @owner"))
            accounts = await accountDelete.ExecuteNonQueryAsync(ct);
        var workState = 0;
        foreach (var table in new[] { "connector_authorizations", "triage_decisions", "connector_actions", "user_settings",
                     "assistant_usage", "reminders", "push_subscriptions" })
        {
            await using var delete = Command(actor, tenantId, ownerSubject,
                $"DELETE FROM {table} WHERE tenant_id = @tenant AND owner_sub = @owner");
            workState += await delete.ExecuteNonQueryAsync(ct);
        }

        await actor.CommitAsync(ct);
        return new PersonalDataErasureResult(memories, consents, DateTimeOffset.UtcNow, accounts, workState);
    }
}
