using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

/// <summary>One sealed settings document per actor (FORCE RLS); AAD binds it to tenant and owner.</summary>
public sealed class PostgresSettingsStore(NpgsqlDataSource db, IEnvelopeEncryption crypto) : ISettingsStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<UserSettings?> GetAsync(string tenantId, string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT sealed_settings FROM user_settings WHERE tenant_id = @tenant AND owner_sub = @owner
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        var envelope = await command.ExecuteScalarAsync(ct) as byte[];
        await actor.CommitAsync(ct);
        return envelope is null ? null : Open(crypto, envelope, tenantId, ownerSubject);
    }

    public async Task SaveAsync(string tenantId, string ownerSubject, UserSettings settings, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        byte[] envelope;
        try { envelope = crypto.Seal(plain, Scope(tenantId, ownerSubject)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO user_settings (tenant_id, owner_sub, sealed_settings, onboarding_completed_at)
            VALUES (@tenant, @owner, @sealed, @onboarded)
            ON CONFLICT (tenant_id, owner_sub) DO UPDATE SET
                sealed_settings = EXCLUDED.sealed_settings,
                onboarding_completed_at = EXCLUDED.onboarding_completed_at,
                updated_at = now()
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("sealed", envelope);
        command.Parameters.AddWithValue("onboarded", (object?)settings.OnboardingCompletedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        await actor.CommitAsync(ct);
    }

    internal static UserSettings Open(IEnvelopeEncryption crypto, byte[] envelope, string tenantId, string ownerSubject)
    {
        var plain = crypto.Open(envelope, Scope(tenantId, ownerSubject));
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(plain, Json)
                ?? throw new CryptographicException("Invalid sealed settings.");
        }
        catch (JsonException) { throw new CryptographicException("Invalid sealed settings."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static EncryptionScope Scope(string tenantId, string ownerSubject) =>
        new(tenantId, ownerSubject, "user.settings", "settings");
}
