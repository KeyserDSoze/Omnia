using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Tao.Domain;

namespace Tao.Infrastructure.Push;

/// <summary>Message encryption for Web Push (RFC 8291, content coding aes128gcm of RFC 8188), one record.</summary>
public static class WebPushEncryption
{
    public const int MaxPlaintext = 3993;
    private const int RecordSize = 4096;

    /// <summary>
    /// The body to POST to a subscription. <paramref name="sender"/> and <paramref name="salt"/> exist for the
    /// RFC test vector; in production they are fresh for every message.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] userAgentPublic, byte[] authSecret,
        ECDiffieHellman? sender = null, byte[]? salt = null)
    {
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException("payload_too_large");
        if (userAgentPublic is not { Length: 65 } || userAgentPublic[0] != 0x04 || authSecret is not { Length: 16 })
            throw new ArgumentException("invalid_subscription");
        var ownKey = sender ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var senderPublic = Uncompressed(ownKey);
            // Creating the key validates that the point is on the curve.
            ECDiffieHellman peer;
            try
            {
                peer = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = userAgentPublic[1..33], Y = userAgentPublic[33..65] }
                });
            }
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
            {
                throw new ArgumentException("invalid_subscription", e);
            }
            using var _ = peer;
            var secret = ownKey.DeriveRawSecretAgreement(peer.PublicKey);
            var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, secret, authSecret);
            var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), userAgentPublic, senderPublic);
            var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

            var recordSalt = salt ?? RandomNumberGenerator.GetBytes(16);
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, recordSalt);
            var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
            var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

            var padded = new byte[plaintext.Length + 1];
            plaintext.CopyTo(padded);
            padded[^1] = 0x02;
            var sealedData = new byte[padded.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, sealedData, tag);

            var header = new byte[16 + 4 + 1 + 65];
            recordSalt.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
            header[20] = 65;
            senderPublic.CopyTo(header, 21);
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(cek);
            return Concat(header, sealedData, tag);
        }
        finally { if (sender is null) ownKey.Dispose(); }
    }

    public static byte[] Uncompressed(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return Concat([0x04], p.Q.X!, p.Q.Y!);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

/// <summary>The server's identity towards the push services (VAPID, RFC 8292): an ECDSA P-256 key.</summary>
public sealed class VapidKeys : IDisposable
{
    private readonly ECDsa _key;

    private VapidKeys(ECDsa key) => _key = key;

    public static VapidKeys Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static VapidKeys FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new VapidKeys(key);
    }

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    /// <summary>The uncompressed public key in base64url: the applicationServerKey of the browser.</summary>
    public string PublicKey
    {
        get
        {
            var p = _key.ExportParameters(false);
            return PushSubscriptionRules.Encode([0x04, .. p.Q.X!, .. p.Q.Y!]);
        }
    }

    /// <summary>The Authorization header value: a short-lived ES256 token for the push service of the endpoint.</summary>
    public string AuthorizationHeader(Uri endpoint, string subject, DateTimeOffset now)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var header = PushSubscriptionRules.Encode(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = PushSubscriptionRules.Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject
        }));
        var signature = _key.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{PushSubscriptionRules.Encode(signature)}, k={PublicKey}";
    }

    public void Dispose() => _key.Dispose();
}
