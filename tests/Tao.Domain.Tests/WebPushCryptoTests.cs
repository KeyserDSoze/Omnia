using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tao.Domain;
using Tao.Infrastructure.Push;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class WebPushCryptoTests
{
    private static byte[] B(string base64Url) => PushSubscriptionRules.Decode(base64Url)!;

    // RFC 8291 section 5 and appendix A.
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";

    private static ECDiffieHellman Sender()
    {
        var pub = B(AsPublic);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B(AsPrivate),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }
        });
    }

    [Fact]
    public void ReproducesTheMessageOfRfc8291ByteForByte()
    {
        using var sender = Sender();
        var body = WebPushEncryption.Encrypt(Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"),
            B(UaPublic), B(Auth), sender, B(Salt));
        Assert.Equal(B(Header).Concat(B(Ciphertext)).ToArray(), body);
        Assert.Equal(86 + 58, body.Length);
    }

    [Fact]
    public void EveryMessageUsesAFreshKeyAndSaltAndTheRecipientCanOpenIt()
    {
        var uaKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPushEncryption.Uncompressed(uaKey);
        var auth = RandomNumberGenerator.GetBytes(16);
        var plain = Encoding.UTF8.GetBytes("Telefona a Marco ☎");
        var first = WebPushEncryption.Encrypt(plain, uaPublic, auth);
        var second = WebPushEncryption.Encrypt(plain, uaPublic, auth);
        Assert.NotEqual(first, second);
        Assert.Equal(plain, Decrypt(first, uaKey, auth));
    }

    [Fact]
    public void RefusesWhatCannotBeSentSafely()
    {
        var auth = RandomNumberGenerator.GetBytes(16);
        var uaPublic = B(UaPublic);
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintext + 1], uaPublic, auth));
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt([1], uaPublic[..64], auth));
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt([1], uaPublic, auth[..15]));
        // A point that is not on the curve must never be used.
        var off = (byte[])uaPublic.Clone();
        off[64] ^= 0x01;
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt([1], off, auth));
        WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintext], uaPublic, auth);
    }

    [Fact]
    public void VapidTokensAreShortLivedAndSignedForTheEndpointsServiceOnly()
    {
        using var keys = VapidKeys.Generate();
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var header = keys.AuthorizationHeader(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "https://taoai.me", now);
        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith(", k=" + keys.PublicKey, header);
        var jwt = header["vapid t=".Length..header.IndexOf(',')].Split('.');
        Assert.Equal("""{"typ":"JWT","alg":"ES256"}""", Encoding.UTF8.GetString(B(jwt[0])));
        using var claims = JsonDocument.Parse(B(jwt[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("https://taoai.me", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

        var pub = B(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), B(jwt[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void TheKeyRoundTripsThroughItsStoredForm()
    {
        using var keys = VapidKeys.Generate();
        using var again = VapidKeys.FromPkcs8(keys.ExportPkcs8());
        Assert.Equal(keys.PublicKey, again.PublicKey);
        Assert.Equal(87, keys.PublicKey.Length);
    }

    /// <summary>The user agent side of RFC 8291, used only to check that a message can be read back.</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] auth)
    {
        var salt = body[..16];
        var asPublic = body[21..86];
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] }
        });
        var secret = uaKey.DeriveRawSecretAgreement(peer.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(WebPushEncryption.Uncompressed(uaKey)).Concat(asPublic).ToArray();
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, secret, auth), 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var data = body[86..^16];
        var plain = new byte[data.Length];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, data, body[^16..], plain);
        Assert.Equal(0x02, plain[^1]);
        return plain[..^1];
    }
}
