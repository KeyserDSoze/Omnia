using System.Net;
using System.Net.Sockets;

namespace Tao.Domain;

/// <summary>An address typed by the user was refused. The code is stable and safe to show.</summary>
public sealed class OutboundBlockedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// What Tao may contact when the address comes from the user (MCP server, direct API): HTTPS
/// only, public addresses only. Without it a pasted URL could reach the cloud metadata service
/// or an internal network. Pure rules, no I/O.
/// </summary>
public static class OutboundPolicy
{
    public const int MaxRedirects = 3;
    public const int MaxUrlLength = 2048;

    private static readonly string[] ForbiddenSuffixes =
        [".localhost", ".local", ".internal", ".localdomain", ".home.arpa", ".lan", ".corp"];

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv6Teredo) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address.GetAddressBytes()),
            _ => false
        };
    }

    /// <summary>Parses the address and refuses anything that is not a public HTTPS endpoint.</summary>
    public static Uri RequireHttpsUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxUrlLength ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            throw new OutboundBlockedException("url_invalid");
        if (uri.Scheme != Uri.UriSchemeHttps) throw new OutboundBlockedException("https_required");
        if (uri.UserInfo.Length > 0 || uri.Host.Length == 0) throw new OutboundBlockedException("url_invalid");
        if (IsForbiddenHost(uri)) throw new OutboundBlockedException("address_not_allowed");
        return uri;
    }

    private static bool IsForbiddenHost(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return !IPAddress.TryParse(uri.DnsSafeHost, out var literal) || !IsPublicAddress(literal);
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || !host.Contains('.')) return true;
        return ForbiddenSuffixes.Any(host.EndsWith);
    }

    private static bool IsPublicV4(byte[] b) => !(
        b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
        (b[0] == 100 && b[1] is >= 64 and <= 127) ||
        (b[0] == 169 && b[1] == 254) ||
        (b[0] == 172 && b[1] is >= 16 and <= 31) ||
        (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) ||
        (b[0] == 192 && b[1] == 88 && b[2] == 99) ||
        (b[0] == 192 && b[1] == 168) ||
        (b[0] == 198 && b[1] is 18 or 19) ||
        (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
        (b[0] == 203 && b[1] == 0 && b[2] == 113));

    private static bool IsPublicV6(byte[] b)
    {
        static bool Zero(ReadOnlySpan<byte> span) { foreach (var x in span) if (x != 0) return false; return true; }
        if (Zero(b.AsSpan(0, 15)) && b[15] <= 1) return false;                               // ::, ::1
        if (b[0] == 0xfe && (b[1] & 0xc0) is 0x80 or 0xc0) return false;                    // link-local, site-local
        if ((b[0] & 0xfe) == 0xfc || b[0] == 0xff) return false;                             // unique-local, multicast
        if (Zero(b.AsSpan(0, 12))) return IsPublicV4(b[12..]);                               // ::a.b.c.d
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && Zero(b.AsSpan(4, 8)))
            return IsPublicV4(b[12..]);                                                      // 64:ff9b::/96 (NAT64)
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b[2..6]);                        // 6to4
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;            // Teredo
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;      // documentation
        if (b[0] == 0x01 && b[1] == 0 && Zero(b.AsSpan(2, 6))) return false;                 // 100::/64 discard
        return true;
    }
}
