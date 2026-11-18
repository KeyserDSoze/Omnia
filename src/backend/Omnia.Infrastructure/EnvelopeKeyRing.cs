using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Omnia.Infrastructure;

/// <summary>
/// In-memory copy of externally supplied KEKs. No default keys, plaintext
/// passwords or embedded development secrets. Dispose to zero the key copies.
/// For production use an appropriately audited KMS/HSM adapter instead.
/// </summary>
public sealed class EnvelopeKeyRing : IDisposable
{
    private readonly Dictionary<string, byte[]> _keys;
    private bool _disposed;
    private static readonly Regex SafeKeyId =
        new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$", RegexOptions.CultureInvariant);

    private EnvelopeKeyRing(string activeKeyId, Dictionary<string, byte[]> keys)
    {
        ActiveKeyId = activeKeyId;
        _keys = keys;
    }

    public string ActiveKeyId { get; }

    public static EnvelopeKeyRing FromFile(string absoluteFilePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath) ||
            !Path.IsPathFullyQualified(absoluteFilePath))
            throw new ArgumentException("Use an absolute path for the external key-ring file.",
                nameof(absoluteFilePath));
        var file = new FileInfo(absoluteFilePath);
        if (!file.Exists || file.Length is < 1 or > 16384)
            throw new InvalidOperationException("Key-ring file is missing or exceeds the allowed size.");
        // Never include the file content or filesystem path in exception messages/logs.
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(absoluteFilePath));
            var obj = json.RootElement;
            if (obj.ValueKind != JsonValueKind.Object ||
                !obj.TryGetProperty("activeKeyId", out var active) ||
                active.ValueKind != JsonValueKind.String ||
                !obj.TryGetProperty("keys", out var entries) ||
                entries.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid external key-ring schema.");
            var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in entries.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.String ||
                    !dictionary.TryAdd(pair.Name, pair.Value.GetString() ?? ""))
                    throw new ArgumentException("Invalid external key-ring entry.");
            }
            return FromBase64(active.GetString() ?? "", dictionary);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid external key-ring JSON.");
        }
    }

    public static EnvelopeKeyRing FromBase64(string activeKeyId,
        IReadOnlyDictionary<string, string> base64Keys)
    {
        ArgumentNullException.ThrowIfNull(base64Keys);
        if (!ValidKeyId(activeKeyId) ||
            base64Keys.Count is < 1 or > 16 || !base64Keys.ContainsKey(activeKeyId))
            throw new ArgumentException("Invalid active key ID or key-ring membership.");

        var copies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var (id, encoded) in base64Keys)
            {
                if (!ValidKeyId(id))
                    throw new ArgumentException("Invalid encryption key ID.");
                byte[] decoded;
                try { decoded = Convert.FromBase64String(encoded); }
                catch (FormatException)
                {
                    throw new ArgumentException("Key is not valid base64.");
                }
                if (decoded.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(decoded);
                    throw new ArgumentException("Encryption KEK must be exactly 256 bits.");
                }
                copies.Add(id, decoded);
            }
            return new EnvelopeKeyRing(activeKeyId, copies);
        }
        catch
        {
            foreach (var key in copies.Values) CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    internal static bool ValidKeyId(string? value) =>
        value is not null && SafeKeyId.IsMatch(value);

    internal byte[] CopyKey(string id)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EnvelopeKeyRing));
        if (!_keys.TryGetValue(id, out var key))
            throw new CryptographicException("Unknown envelope encryption key.");
        return key.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }
}
