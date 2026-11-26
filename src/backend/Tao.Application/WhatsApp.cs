using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tao.Application;

/// <summary>A text message that a person sent to the Tao WhatsApp number.</summary>
public sealed record WhatsAppMessage(string From, string MessageId, string Text, DateTimeOffset At);

/// <summary>
/// Rules for the WhatsApp Business Cloud API webhook (ADR 0111): the signature proves Meta sent
/// the call, the payload is read defensively, and only plain text is kept. No I/O.
/// </summary>
public static class WhatsAppWebhook
{
    public const int MaxTextLength = 4096;

    /// <summary>X-Hub-Signature-256: "sha256=" plus the hex HMAC-SHA256 of the raw body with the app secret.</summary>
    public static bool SignatureIsValid(ReadOnlySpan<byte> body, string? header, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || header is null ||
            !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header.AsSpan(7)); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>Subscription handshake: echo the challenge only for the configured verify token.</summary>
    public static string? Challenge(string? mode, string? token, string? challenge, string expectedToken)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(token) ||
            string.IsNullOrEmpty(challenge)) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expectedToken))
            ? challenge : null;
    }

    public static IReadOnlyList<WhatsAppMessage> Messages(ReadOnlySpan<byte> body)
    {
        var result = new List<WhatsAppMessage>();
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            foreach (var entry in Array(document.RootElement, "entry"))
            foreach (var change in Array(entry, "changes"))
            {
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                foreach (var message in Array(value, "messages"))
                    if (Read(message) is { } read) result.Add(read);
            }
        }
        catch (JsonException) { return []; }
        return result;
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray() : [];

    private static WhatsAppMessage? Read(JsonElement message)
    {
        string? Text(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        if (Text(message, "type") != "text" || !message.TryGetProperty("text", out var text)) return null;
        var from = Text(message, "from");
        var id = Text(message, "id");
        var body = Text(text, "body")?.Trim();
        if (from is not { Length: >= 7 and <= 15 } || !from.All(char.IsAsciiDigit)) return null;
        if (string.IsNullOrEmpty(id) || id.Length > 128 || string.IsNullOrEmpty(body)) return null;
        if (body.Length > MaxTextLength) body = body[..MaxTextLength];
        var at = long.TryParse(Text(message, "timestamp"), out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UnixEpoch;
        return new WhatsAppMessage(from, id, body, at);
    }
}

/// <summary>
/// The code an unknown sender gets and the owner approves in the app (like OpenClaw's pairing):
/// 8 characters without look-alikes, valid one hour, at most three open requests per number.
/// Only a scoped hash is stored.
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 8;
    public const int MaxPending = 3;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>Upper case, without spaces and hyphens; null when it cannot be a code.</summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var code = new string(typed.Where(c => c is not (' ' or '-')).ToArray()).ToUpperInvariant();
        return code.Length == Length && code.All(Alphabet.Contains) ? code : null;
    }

    public static string Hash(string code, string scope) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"tao.pairing|{scope}|{code}"))).ToLowerInvariant();
}
