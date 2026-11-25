using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tao.Application;
using Tao.Infrastructure;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// The codec is deliberately tested without fake crypto, mocked AEAD or secrets in git.
/// The PostgreSQL raw/normalized/embedding columns are NOT encrypted by this slice.
/// </summary>
public sealed class EnvelopeEncryptionTests
{
    private static string FreshKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static EncryptionScope Scope(string tenant = "tenant-a", string subject = "user-a",
        string purpose = "memory.raw", string record = "record-1") =>
        new(tenant, subject, purpose, record);

    [Fact]
    public void BinaryRoundTripUsesRandomizedEnvelopeAndDoesNotExposePlaintext()
    {
        var ring = EnvelopeKeyRing.FromBase64("key-2026",
            new Dictionary<string, string> { ["key-2026"] = FreshKey() });
        IEnvelopeEncryption crypto = new AesGcmEnvelopeEncryption(ring);
        var plaintext = Encoding.UTF8.GetBytes("Confidential synthetic binary \0 with unicode 🔐");
        var sealedOne = crypto.Seal(plaintext, Scope());
        var sealedTwo = crypto.Seal(plaintext, Scope());

        Assert.NotEqual(Convert.ToBase64String(sealedOne), Convert.ToBase64String(sealedTwo));
        Assert.NotEqual(Convert.ToBase64String(plaintext), Convert.ToBase64String(sealedOne));
        Assert.True(sealedOne.Length > plaintext.Length + 50);
        Assert.Equal(plaintext, crypto.Open(sealedOne, Scope()));
        Assert.Equal(plaintext, crypto.Open(sealedTwo, Scope()));
        Assert.DoesNotContain("Confidential", Encoding.UTF8.GetString(sealedOne));
    }

    [Fact]
    public void TamperAndCrossIdentityPurposeAndRecordFailClosed()
    {
        IEnvelopeEncryption crypto = new AesGcmEnvelopeEncryption(
            EnvelopeKeyRing.FromBase64("v1", new Dictionary<string, string>
            { ["v1"] = FreshKey() }));
        var sealedBytes = crypto.Seal(Encoding.UTF8.GetBytes("synthetic sensitive"), Scope());

        foreach (var other in new[] {
            Scope(tenant: "tenant-b"), Scope(subject: "user-b"),
            Scope(purpose: "memory.normalized"), Scope(record: "record-2"),
            // Length-prefix prevents ambiguous concatenations across fields.
            Scope(tenant: "tenant", subject: "auser-a")
        })
            Assert.ThrowsAny<CryptographicException>(() => crypto.Open(sealedBytes, other));

        foreach (var offset in new[] { 0, 4, 9, 25, sealedBytes.Length - 1 })
        {
            var tampered = sealedBytes.ToArray();
            tampered[offset] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => crypto.Open(tampered, Scope()));
        }
        Assert.ThrowsAny<CryptographicException>(() => crypto.Open([1, 2, 3], Scope()));
        Assert.ThrowsAny<CryptographicException>(() =>
            crypto.Open(sealedBytes.AsSpan(0, sealedBytes.Length - 5).ToArray(), Scope()));
    }

    [Fact]
    public void RewrapRotatesKekWithoutChangingEncryptedPayloadAndSupportsRestore()
    {
        var oldKey = FreshKey();
        var newKey = FreshKey();
        var oldOnly = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("old", new Dictionary<string, string>()
        {
            ["old"] = oldKey
        }));
        var oldAndNew = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("new", new Dictionary<string, string>()
        {
            ["old"] = oldKey, ["new"] = newKey
        }));
        var newOnly = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("new", new Dictionary<string, string>()
        {
            ["new"] = newKey
        }));
        var scope = Scope();
        var plaintext = RandomNumberGenerator.GetBytes(8192);
        var existing = oldOnly.Seal(plaintext, scope);
        var rewrapped = oldAndNew.Rewrap(existing, scope);

        Assert.Equal(plaintext, oldOnly.Open(existing, scope));
        Assert.Equal(plaintext, oldAndNew.Open(existing, scope));
        Assert.Equal(plaintext, newOnly.Open(rewrapped, scope));
        Assert.ThrowsAny<CryptographicException>(() => oldOnly.Open(rewrapped, scope));

        // After full rewrap, a restarted app with only the new KEK restores payload.
        var afterRestart = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64(
            "new", new Dictionary<string, string>() { ["new"] = newKey }));
        Assert.Equal(plaintext, afterRestart.Open(rewrapped, scope));
        Assert.Equal(existing.Length, rewrapped.Length); // IDs have equal length.
        Assert.Equal(existing.AsSpan(existing.Length - plaintext.Length).ToArray(),
            rewrapped.AsSpan(rewrapped.Length - plaintext.Length).ToArray());
        Assert.Equal(rewrapped, newOnly.Rewrap(rewrapped, scope)); // stable idempotence
    }

    [Fact]
    public void KeyFileRejectsMissingMalformedShortAndUnrecognizedKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "tao-crypto-" + Guid.NewGuid() + ".json");
        try
        {
            Assert.ThrowsAny<Exception>(() => EnvelopeKeyRing.FromFile(path));
            File.WriteAllText(path, "{invalid json");
            Assert.ThrowsAny<Exception>(() => EnvelopeKeyRing.FromFile(path));
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                activeKeyId = "v1", keys = new { v1 = Convert.ToBase64String(new byte[12]) }
            }));
            Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromFile(path));

            File.WriteAllText(path, JsonSerializer.Serialize(new {
                activeKeyId = "not-present", keys = new Dictionary<string, string> {
                    ["v1"] = FreshKey()
                }
            }));
            Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromFile(path));

            var key = FreshKey();
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                activeKeyId = "v1", keys = new Dictionary<string, string> {
                    ["v1"] = key
                }
            }));
            IEnvelopeEncryption encryption = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromFile(path));
            var plaintext = Encoding.UTF8.GetBytes("synthetic private");
            Assert.Equal(plaintext, encryption.Open(encryption.Seal(plaintext, Scope()), Scope()));
            Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromFile("relative/keyring.json"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InvalidKeysScopesAndUnknownDecryptionKeysAreRejected()
    {
        Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromBase64("v1", new Dictionary<string, string>()
        { ["v1"] = Convert.ToBase64String(new byte[24]) }));
        Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromBase64("unknown", new Dictionary<string, string>()
        { ["v1"] = FreshKey() }));
        Assert.Throws<ArgumentException>(() => EnvelopeKeyRing.FromBase64("../escape", new Dictionary<string, string>()
        { ["../escape"] = FreshKey() }));
        var first = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("v1", new Dictionary<string, string>()
        { ["v1"] = FreshKey() }));
        var second = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("v2", new Dictionary<string, string>()
        { ["v2"] = FreshKey() }));
        var sealedBytes = first.Seal([1, 2, 3], Scope());
        Assert.ThrowsAny<CryptographicException>(() => second.Open(sealedBytes, Scope()));
        Assert.ThrowsAny<CryptographicException>(() => second.Rewrap(sealedBytes, Scope()));
        Assert.Throws<ArgumentException>(() =>
            first.Seal([1, 2], new EncryptionScope("", "user", "memory.raw", "id")));
    }
}
