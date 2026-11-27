using System.Security.Cryptography;
using Npgsql;
using Tao.Application;

namespace Tao.Infrastructure.Push;

/// <summary>
/// The server's VAPID key lives sealed in the database, so every replica (cloud or on-prem) shares it without
/// extra configuration. Replacing it invalidates every subscription: browsers are bound to the public key.
/// </summary>
public sealed class VapidKeyProvider(NpgsqlDataSource db, IEnvelopeEncryption crypto) : IVapidPublicKey
{
    private static readonly EncryptionScope Scope = new("_system", "vapid", "push.vapid", "1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VapidKeys? _keys;

    public async Task<VapidKeys> KeysAsync(CancellationToken ct)
    {
        if (_keys is not null) return _keys;
        await _gate.WaitAsync(ct);
        try
        {
            if (_keys is not null) return _keys;
            var stored = await LoadAsync(ct);
            if (stored is null)
            {
                using var fresh = VapidKeys.Generate();
                var plain = fresh.ExportPkcs8();
                byte[] envelope;
                try { envelope = crypto.Seal(plain, Scope); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                await using (var insert = db.CreateCommand("""
                    INSERT INTO push_settings (id, sealed_vapid_key) VALUES (1, @sealed) ON CONFLICT (id) DO NOTHING
                    """))
                {
                    insert.Parameters.AddWithValue("sealed", envelope);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                // Another replica may have won the race: always use what is stored.
                stored = await LoadAsync(ct) ?? throw new InvalidOperationException("The VAPID key could not be stored.");
            }
            var key = crypto.Open(stored, Scope);
            try { _keys = VapidKeys.FromPkcs8(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return _keys;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAsync(CancellationToken ct) => (await KeysAsync(ct)).PublicKey;

    private async Task<byte[]?> LoadAsync(CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT sealed_vapid_key FROM push_settings WHERE id = 1");
        return await command.ExecuteScalarAsync(ct) as byte[];
    }
}
