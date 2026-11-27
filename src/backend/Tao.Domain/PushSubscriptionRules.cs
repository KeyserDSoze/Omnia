using System.Security.Cryptography;

namespace Tao.Domain;

/// <summary>A browser's Web Push subscription as received from the user's own device.</summary>
public sealed record PushSubscriptionInput(string Endpoint, string P256dh, string Auth);

/// <summary>
/// The server posts to the address of a subscription, so only the push services of the browsers are accepted:
/// HTTPS, no credentials, a known host. The keys must have the exact shape RFC 8291 expects.
/// </summary>
public static class PushSubscriptionRules
{
    public const int MaxPerActor = 5;
    public const int MaxEndpointLength = 2048;

    private static readonly string[] ExactHosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com", "web.push.apple.com"];
    private static readonly string[] HostSuffixes = [".push.services.mozilla.com", ".notify.windows.com", ".push.apple.com"];

    public static bool IsKnownPushService(string host) =>
        ExactHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        HostSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Throws <see cref="ArgumentException"/> with <c>invalid_subscription</c>.</summary>
    public static PushSubscriptionInput Validate(string? endpoint, string? p256dh, string? auth)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaxEndpointLength ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort || !IsKnownPushService(uri.Host))
            throw new ArgumentException("invalid_subscription");
        var key = Decode(p256dh);
        if (key is not { Length: 65 } || key[0] != 0x04 || Decode(auth) is not { Length: 16 })
            throw new ArgumentException("invalid_subscription");
        return new PushSubscriptionInput(uri.AbsoluteUri, p256dh!, auth!);
    }

    public static byte[]? Decode(string? base64Url)
    {
        if (string.IsNullOrEmpty(base64Url) || base64Url.Length > 200) return null;
        var text = base64Url.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        text += new string('=', (4 - text.Length % 4) % 4);
        try { return Convert.FromBase64String(text); } catch (FormatException) { return null; }
    }

    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] EndpointHash(string endpoint) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint));
}
