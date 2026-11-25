using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tao.Application;

namespace Tao.Infrastructure;

/// <summary>
/// Actor-scoped (FORCE RLS) storage of pending OAuth authorizations and connected accounts.
/// PKCE verifiers and OAuth credentials are sealed with the envelope key ring; the AEAD
/// associated data binds tenant, owner, purpose and row identity, so a ciphertext cannot be
/// replayed into another row or another actor.
/// </summary>
public sealed class PostgresConnectorStore(NpgsqlDataSource db, IEnvelopeEncryption crypto) : IConnectorStore
{
    public const int MaxPendingAuthorizations = 20;

    public async Task SaveAuthorizationAsync(string tenantId, string ownerSubject, string provider,
        byte[] stateHash, string codeVerifier, DateTimeOffset expiresAt, CancellationToken ct)
    {
        if (stateHash is not { Length: 32 }) throw new ArgumentException("Invalid state hash.", nameof(stateHash));
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        // Abandoned flows: expired rows are dropped and only the most recent ones are kept.
        await using (var cleanup = new NpgsqlCommand("""
            DELETE FROM connector_authorizations
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND (expires_at <= now() OR state_hash IN (
                    SELECT state_hash FROM connector_authorizations
                    WHERE tenant_id = @tenant AND owner_sub = @owner
                    ORDER BY expires_at DESC, state_hash OFFSET @keep))
            """, actor.Connection, actor.Transaction))
        {
            Actor(cleanup, tenantId, ownerSubject);
            cleanup.Parameters.AddWithValue("keep", MaxPendingAuthorizations - 1);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        var verifier = Encoding.UTF8.GetBytes(codeVerifier);
        byte[] envelope;
        try { envelope = crypto.Seal(verifier, AuthorizationScope(tenantId, ownerSubject, stateHash)); }
        finally { CryptographicOperations.ZeroMemory(verifier); }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO connector_authorizations
                (state_hash, tenant_id, owner_sub, provider, sealed_verifier, expires_at)
            VALUES (@hash, @tenant, @owner, @provider, @sealed, @expires)
            """, actor.Connection, actor.Transaction))
        {
            Actor(insert, tenantId, ownerSubject);
            insert.Parameters.AddWithValue("hash", stateHash);
            insert.Parameters.AddWithValue("provider", provider);
            insert.Parameters.AddWithValue("sealed", envelope);
            insert.Parameters.AddWithValue("expires", expiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
    }

    public async Task<PendingAuthorization?> ConsumeAuthorizationAsync(string tenantId, string ownerSubject,
        byte[] stateHash, CancellationToken ct)
    {
        if (stateHash is not { Length: 32 }) return null;
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            DELETE FROM connector_authorizations
            WHERE state_hash = @hash AND tenant_id = @tenant AND owner_sub = @owner
            RETURNING provider, sealed_verifier, expires_at
            """, actor.Connection, actor.Transaction);
        Actor(command, tenantId, ownerSubject);
        command.Parameters.AddWithValue("hash", stateHash);
        PendingAuthorization? pending = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var plain = crypto.Open(reader.GetFieldValue<byte[]>(1),
                    AuthorizationScope(tenantId, ownerSubject, stateHash));
                try
                {
                    pending = new PendingAuthorization(reader.GetString(0), Encoding.UTF8.GetString(plain),
                        reader.GetFieldValue<DateTimeOffset>(2));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        await actor.CommitAsync(ct);
        return pending;
    }

    public async Task<ConnectorAccountSummary> UpsertAccountAsync(string tenantId, string ownerSubject,
        string provider, ConnectorCredentials credentials, CancellationToken ct)
    {
        var externalId = credentials.Identity.ExternalId;
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 255)
            throw new ConnectorUnavailableException("invalid_identity");
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);

        Guid? existing = null;
        DateTimeOffset connectedAt = default;
        await using (var select = new NpgsqlCommand("""
            SELECT id, connected_at FROM connector_accounts
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND provider = @provider AND external_account_id = @external
            FOR UPDATE
            """, actor.Connection, actor.Transaction))
        {
            Actor(select, tenantId, ownerSubject);
            select.Parameters.AddWithValue("provider", provider);
            select.Parameters.AddWithValue("external", externalId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                (existing, connectedAt) = (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
        }

        // Reconnecting the same provider identity keeps the account id (and its triage history).
        var id = existing ?? Guid.NewGuid();
        var envelope = Seal(credentials, tenantId, ownerSubject, id);
        await using (var write = new NpgsqlCommand(existing is null
            ? """
              INSERT INTO connector_accounts
                  (id, tenant_id, owner_sub, provider, external_account_id, sealed_credentials)
              VALUES (@id, @tenant, @owner, @provider, @external, @sealed)
              RETURNING connected_at
              """
            : """
              UPDATE connector_accounts SET sealed_credentials = @sealed, updated_at = now()
              WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
              RETURNING connected_at
              """, actor.Connection, actor.Transaction))
        {
            Actor(write, tenantId, ownerSubject);
            write.Parameters.AddWithValue("id", id);
            write.Parameters.AddWithValue("sealed", envelope);
            if (existing is null)
            {
                write.Parameters.AddWithValue("provider", provider);
                write.Parameters.AddWithValue("external", externalId);
            }
            await using var reader = await write.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Connector account was not written.");
            connectedAt = reader.GetFieldValue<DateTimeOffset>(0);
        }
        await actor.CommitAsync(ct);
        return Summary(id, provider, credentials, connectedAt);
    }

    public async Task<IReadOnlyList<ConnectorAccountSummary>> ListAccountsAsync(string tenantId,
        string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, provider, sealed_credentials, connected_at FROM connector_accounts
            WHERE tenant_id = @tenant AND owner_sub = @owner
            ORDER BY connected_at, id
            LIMIT 50
            """, actor.Connection, actor.Transaction);
        Actor(command, tenantId, ownerSubject);
        var accounts = new List<ConnectorAccountSummary>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var credentials = Open(reader.GetFieldValue<byte[]>(2), tenantId, ownerSubject, id);
                accounts.Add(Summary(id, reader.GetString(1), credentials,
                    reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }
        await actor.CommitAsync(ct);
        return accounts;
    }

    public async Task<RemovedConnectorAccount?> DeleteAccountAsync(string tenantId, string ownerSubject,
        Guid accountId, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        RemovedConnectorAccount? removed = null;
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM connector_accounts WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
            RETURNING provider, sealed_credentials
            """, actor.Connection, actor.Transaction))
        {
            Actor(delete, tenantId, ownerSubject);
            delete.Parameters.AddWithValue("id", accountId);
            await using var reader = await delete.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                removed = new RemovedConnectorAccount(reader.GetString(0),
                    Open(reader.GetFieldValue<byte[]>(1), tenantId, ownerSubject, accountId));
        }
        if (removed is null) return null;

        // Decisions about this account's items are meaningless once it is disconnected.
        await using (var decisions = new NpgsqlCommand("""
            DELETE FROM triage_decisions
            WHERE tenant_id = @tenant AND owner_sub = @owner AND left(item_key, length(@prefix)) = @prefix
            """, actor.Connection, actor.Transaction))
        {
            Actor(decisions, tenantId, ownerSubject);
            decisions.Parameters.AddWithValue("prefix", $"{removed.Provider}:{accountId:N}:");
            await decisions.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return removed;
    }

    public async Task<ConnectorCredentials?> UseCredentialsAsync(string tenantId, string ownerSubject,
        Guid accountId, Func<ConnectorCredentials, CancellationToken, Task<ConnectorCredentials>> refresh,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        ConnectorCredentials current;
        await using (var select = new NpgsqlCommand("""
            SELECT sealed_credentials FROM connector_accounts
            WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
            FOR UPDATE
            """, actor.Connection, actor.Transaction))
        {
            Actor(select, tenantId, ownerSubject);
            select.Parameters.AddWithValue("id", accountId);
            if (await select.ExecuteScalarAsync(ct) is not byte[] envelope) return null;
            current = Open(envelope, tenantId, ownerSubject, accountId);
        }

        // The row lock serializes concurrent refreshes, so a rotated refresh token is never lost.
        var updated = await refresh(current, ct);
        if (!ReferenceEquals(updated, current))
        {
            await using var write = new NpgsqlCommand("""
                UPDATE connector_accounts SET sealed_credentials = @sealed, updated_at = now()
                WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
                """, actor.Connection, actor.Transaction);
            Actor(write, tenantId, ownerSubject);
            write.Parameters.AddWithValue("id", accountId);
            write.Parameters.AddWithValue("sealed", Seal(updated, tenantId, ownerSubject, accountId));
            await write.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return updated;
    }

    private static ConnectorAccountSummary Summary(Guid id, string provider, ConnectorCredentials credentials,
        DateTimeOffset connectedAt) =>
        new(id, provider, credentials.Identity.DisplayName, credentials.Identity.Email, connectedAt,
            credentials.Tokens.Scopes);

    private byte[] Seal(ConnectorCredentials credentials, string tenantId, string ownerSubject, Guid id)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { return crypto.Seal(plain, CredentialScope(tenantId, ownerSubject, id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private ConnectorCredentials Open(byte[] envelope, string tenantId, string ownerSubject, Guid id)
    {
        var plain = crypto.Open(envelope, CredentialScope(tenantId, ownerSubject, id));
        try
        {
            ConnectorCredentials? credentials;
            try { credentials = JsonSerializer.Deserialize<ConnectorCredentials>(plain); }
            catch (JsonException) { throw new CryptographicException("Invalid sealed connector credentials."); }
            if (credentials?.Tokens?.AccessToken is null || credentials.Identity?.ExternalId is null ||
                credentials.Tokens.Scopes is null)
                throw new CryptographicException("Invalid sealed connector credentials.");
            return credentials;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static EncryptionScope CredentialScope(string tenantId, string ownerSubject, Guid id) =>
        new(tenantId, ownerSubject, "connector.credentials", id.ToString("N"));

    private static EncryptionScope AuthorizationScope(string tenantId, string ownerSubject, byte[] stateHash) =>
        new(tenantId, ownerSubject, "connector.authorization", Convert.ToHexStringLower(stateHash));

    private static void Actor(NpgsqlCommand command, string tenantId, string ownerSubject)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
    }
}
