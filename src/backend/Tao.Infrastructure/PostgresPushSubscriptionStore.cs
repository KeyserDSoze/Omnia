using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

/// <summary>The user's browsers (FORCE RLS): endpoint and keys are sealed, only a hash is stored in the clear.</summary>
public sealed class PostgresPushSubscriptionStore(NpgsqlDataSource db, IEnvelopeEncryption crypto) : IPushSubscriptionStore
{
    private sealed record Sealed(string Endpoint, string P256dh, string Auth);

    public async Task<IReadOnlyList<PushSubscription>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        var rows = new List<PushSubscription>();
        await using (var command = Command(actor, tenantId, ownerSubject, """
            SELECT id, sealed_subscription, created_at, failures FROM push_subscriptions
            WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY created_at, id
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var plain = crypto.Open(reader.GetFieldValue<byte[]>(1), Scope(tenantId, ownerSubject, id));
                try
                {
                    var s = JsonSerializer.Deserialize<Sealed>(plain) ?? throw new CryptographicException("Invalid sealed subscription.");
                    rows.Add(new PushSubscription(id, s.Endpoint, s.P256dh, s.Auth, reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3)));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        await actor.CommitAsync(ct);
        return rows;
    }

    public async Task<PushSubscription> UpsertAsync(string tenantId, string ownerSubject, PushSubscriptionInput input, CancellationToken ct)
    {
        var hash = PushSubscriptionRules.EndpointHash(input.Endpoint);
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        Guid id;
        var existing = false;
        await using (var find = Command(actor, tenantId, ownerSubject,
            "SELECT id FROM push_subscriptions WHERE tenant_id = @tenant AND owner_sub = @owner AND endpoint_hash = @hash"))
        {
            find.Parameters.AddWithValue("hash", hash);
            if (await find.ExecuteScalarAsync(ct) is Guid found) { id = found; existing = true; }
            else id = Guid.NewGuid();
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Sealed(input.Endpoint, input.P256dh, input.Auth));
        byte[] envelope;
        try { envelope = crypto.Seal(plain, Scope(tenantId, ownerSubject, id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        DateTimeOffset createdAt;
        await using (var write = Command(actor, tenantId, ownerSubject, existing
            ? "UPDATE push_subscriptions SET sealed_subscription = @sealed, failures = 0 WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner RETURNING created_at"
            : """
              INSERT INTO push_subscriptions (id, tenant_id, owner_sub, endpoint_hash, sealed_subscription)
              VALUES (@id, @tenant, @owner, @hash, @sealed) RETURNING created_at
              """))
        {
            write.Parameters.AddWithValue("id", id);
            write.Parameters.AddWithValue("sealed", envelope);
            if (!existing) write.Parameters.AddWithValue("hash", hash);
            createdAt = new DateTimeOffset((DateTime)(await write.ExecuteScalarAsync(ct))!);
        }
        // At most MaxPerActor browsers: the oldest ones make room.
        await using (var trim = Command(actor, tenantId, ownerSubject, """
            DELETE FROM push_subscriptions WHERE tenant_id = @tenant AND owner_sub = @owner AND id IN (
                SELECT id FROM push_subscriptions WHERE tenant_id = @tenant AND owner_sub = @owner
                ORDER BY created_at DESC, id OFFSET @keep)
            """))
        {
            trim.Parameters.AddWithValue("keep", PushSubscriptionRules.MaxPerActor);
            await trim.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return new PushSubscription(id, input.Endpoint, input.P256dh, input.Auth, createdAt, 0);
    }

    public async Task<bool> RemoveAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        int rows;
        await using (var command = Command(actor, tenantId, ownerSubject,
            "DELETE FROM push_subscriptions WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id"))
        {
            command.Parameters.AddWithValue("id", id);
            rows = await command.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return rows > 0;
    }

    public async Task RecordOutcomeAsync(string tenantId, string ownerSubject, Guid id, bool success, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using (var command = Command(actor, tenantId, ownerSubject, success
            ? "UPDATE push_subscriptions SET last_success_at = now(), failures = 0 WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id"
            : "UPDATE push_subscriptions SET failures = failures + 1 WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id"))
        {
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
    }

    private static NpgsqlCommand Command(PostgresActorScope actor, string tenantId, string ownerSubject, string sql)
    {
        var command = new NpgsqlCommand(sql, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        return command;
    }

    internal static EncryptionScope Scope(string tenantId, string ownerSubject, Guid id) =>
        new(tenantId, ownerSubject, "push.subscription", id.ToString("D"));
}
