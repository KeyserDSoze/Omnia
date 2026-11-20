using System.Text.Json;
using Npgsql;
using Omnia.Application;

namespace Omnia.Infrastructure;

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
        writer.WriteString("scope", "omnia-postgresql-primary");
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WriteString("tenantId", tenantId);
        writer.WriteString("subject", ownerSubject);
        writer.WritePropertyName("memoryEntries");
        writer.WriteStartArray();

        await using (var command = new NpgsqlCommand("""
            SELECT id, source, external_id, content_type, raw_content,
                   normalized_json::text, summary, tags, occurred_at,
                   received_at, expires_at, embedding::text, embedding_model,
                   sealed_payload
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
                var content = reader.IsDBNull(13)
                    ? new SealedMemoryPayload.Content(reader.GetString(4),
                        reader.GetString(5), reader.GetString(6),
                        reader.GetFieldValue<string[]>(7))
                    : SealedMemoryPayload.Decrypt(crypto, reader.GetFieldValue<byte[]>(13),
                        tenantId, ownerSubject, reader.GetGuid(0));
                writer.WriteString("storageProtection",
                    reader.IsDBNull(13) ? "legacy-plaintext" : "sealed-v1");
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
                writer.WriteString("occurredAt", reader.GetFieldValue<DateTimeOffset>(8));
                writer.WriteString("receivedAt", reader.GetFieldValue<DateTimeOffset>(9));
                writer.WriteString("expiresAt", reader.GetFieldValue<DateTimeOffset>(10));
                writer.WritePropertyName("embedding");
                if (reader.IsDBNull(11))
                    writer.WriteNullValue();
                else
                {
                    // pgvector's text representation is a valid JSON numeric array.
                    // Export derived data too; not just the note's summary.
                    using var vector = JsonDocument.Parse(reader.GetString(11));
                    vector.RootElement.WriteTo(writer);
                }
                if (reader.IsDBNull(12)) writer.WriteNull("embeddingModel");
                else writer.WriteString("embeddingModel", reader.GetString(12));
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

    /// <summary>
    /// Opt-in, CI-only preflight for a synthetic erasure request that has
    /// already been persisted on a second PostgreSQL cluster.
    /// The SAME request UUID is written to the transactional source outbox.
    /// This is NOT an externally authenticated request or commit-proof
    /// and MUST NOT issue a recovery-ready marker or be bound to HTTP.
    /// </summary>
    public async Task<PersonalDataErasureResult> EraseWithWitnessedIntentAsync(
        Guid requestId, string tenantId, string ownerSubject,
        NpgsqlDataSource intentReader, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException(
                "Correlated pre-erase test mode is restricted to CI.");
        if (requestId == Guid.Empty)
            throw new ArgumentException("An intent UUID is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(intentReader);
        // The independent SQL identity check occurs in FindAsync, including
        // owner/admin rejection and EXECUTE-only read privileges.
        var remote = await new PostgresPreEraseIntentReader(intentReader)
            .FindAsync(requestId, ct) ??
            throw new InvalidDataException("ERASURE_INTENT_NOT_FOUND.");
        if (remote.RequestId != requestId || remote.Sequence <= 0 ||
            remote.IntentType != "erase-actor-intent" ||
            !string.Equals(remote.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(remote.OwnerSubject, ownerSubject, StringComparison.Ordinal))
            throw new InvalidDataException("ERASURE_INTENT_SCOPE_MISMATCH.");

        // The primary transaction cannot make the remote record atomic.
        // A failed or ambiguous COMMIT must still be treated as uncertain;
        // source outbox UUID is NOT authoritative after PITR.
        return await EraseCoreAsync(tenantId, ownerSubject, requestId, ct);
    }

    /// <summary>
    /// Stricter synthetic CI-only preflight than EraseWithWitnessedIntentAsync:
    /// requires an HMAC-authenticated THIRD-PGDATA checkpoint of the exact
    /// PRE-intent before opening a primary actor transaction.
    /// Missing/incorrect/stale/unanchored intent MUST NOT erase anything.
    /// The checkpoint does NOT certify source COMMIT or legal authorization.
    /// This method is NOT registered with the production REST API.
    /// </summary>
    public async Task<PersonalDataErasureResult> EraseWithAnchoredIntentAsync(
        Guid requestId, string tenantId, string ownerSubject,
        NpgsqlDataSource intentReader, NpgsqlDataSource anchorReader,
        byte[] signingKey, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException(
                "Anchored intent erasure is synthetic CI-only.");
        if (requestId == Guid.Empty)
            throw new ArgumentException("An intent UUID is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(intentReader);
        ArgumentNullException.ThrowIfNull(anchorReader);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length != 32)
            throw new InvalidDataException("INTENT_ANCHOR_KEY_INVALID.");

        // This verifies the exact requested actor against ONE consistent
        // remote witness snapshot and the previously persisted independent
        // HMAC checkpoint. It cannot detect an anchor rolled back by its
        // owner or provide a distributed atomic transaction.
        await new PostgresPreEraseIntentAnchorVerifier(intentReader, anchorReader)
            .VerifyRequestAsync(requestId, tenantId, ownerSubject, signingKey, ct);

        // Primary DELETE + same-UUID outbox are transactionally atomic here;
        // the remote pre-intent and HMAC checkpoint are NOT part of that tx.
        return await EraseCoreAsync(tenantId, ownerSubject, requestId, ct);
    }

    /// <summary>
    /// Stronger CI-ONLY synthetic preflight: an authenticated remote intent
    /// must already be HMAC-anchored, then a one-time attempt is committed
    /// to THIRD PGDATA before touching source data. Duplicate attempts fail
    /// even if the original primary outbox was lost to physical PITR.
    /// A claimed request is NOT proof of primary DELETE/COMMIT, and a failed
    /// or ambiguous source transaction MUST NOT be retried automatically.
    /// This is NOT exposed by the REST API or IPersonalDataService.
    /// </summary>
    public async Task<PersonalDataErasureResult>
        EraseWithOneShotAnchoredIntentAsync(
            Guid requestId, string tenantId, string ownerSubject,
            NpgsqlDataSource intentReader, NpgsqlDataSource anchorReader,
            NpgsqlDataSource claimWriter, byte[] signingKey,
            CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException(
                "One-shot erasure attempt is synthetic CI-only.");
        if (requestId == Guid.Empty)
            throw new ArgumentException("An intent UUID is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(intentReader);
        ArgumentNullException.ThrowIfNull(anchorReader);
        ArgumentNullException.ThrowIfNull(claimWriter);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length != 32)
            throw new InvalidDataException("INTENT_ANCHOR_KEY_INVALID.");

        // No actor state or outbox is touched before both validations. The
        // one-shot writer role is separate from anchor/witness writers.
        await new PostgresPreEraseIntentAnchorVerifier(intentReader, anchorReader)
            .VerifyRequestAsync(requestId, tenantId, ownerSubject, signingKey, ct);
        await new PostgresPreEraseExecutionClaimWriter(claimWriter).ClaimOnceAsync(
            requestId, tenantId, ownerSubject, signingKey, ct);

        // A remote claim can persist when the following source COMMIT fails,
        // crashes or returns an ambiguous ACK. This is intentional: do NOT
        // auto-retry or auto-erase; operator investigation is mandatory.
        return await EraseCoreAsync(tenantId, ownerSubject, requestId, ct);
    }

    public Task<PersonalDataErasureResult> EraseAsync(string tenantId,
        string ownerSubject, CancellationToken ct) =>
        EraseCoreAsync(tenantId, ownerSubject, Guid.NewGuid(), ct);

    private async Task<PersonalDataErasureResult> EraseCoreAsync(string tenantId,
        string ownerSubject, Guid eventId, CancellationToken ct)
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

        // Insert the erasure event in the SAME actor-scoped transaction
        // as deleting consent + memory. Any failure rolls back all three.
        // Record also zero-row requests: other stores or PITR may hold data.
        // Request-level dedup and external publication are separate gates.
        await using var outbox = new NpgsqlCommand("""
            INSERT INTO public.erasure_outbox
                (id, tenant_id, owner_sub, event_type)
            VALUES (@id, @tenant, @owner, 'erase-actor')
            """, actor.Connection, actor.Transaction);
        // Duplicating a previously committed correlated request triggers
        // unique_violation (23505) and rolls back ALL earlier deletes in
        // this transaction IF the original outbox row still exists.
        // After primary PITR removes that row this constraint is not an
        // anti-rollback authority: NEVER replay pre-intents automatically.
        outbox.Parameters.AddWithValue("id", eventId);
        outbox.Parameters.AddWithValue("tenant", tenantId);
        outbox.Parameters.AddWithValue("owner", ownerSubject);
        await outbox.ExecuteNonQueryAsync(ct);

        await actor.CommitAsync(ct);
        return new PersonalDataErasureResult(memories, consents, DateTimeOffset.UtcNow);
    }
}
