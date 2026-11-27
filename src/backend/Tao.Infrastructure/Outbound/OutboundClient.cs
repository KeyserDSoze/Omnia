using System.Net;
using System.Net.Sockets;
using System.Text;
using Tao.Domain;

namespace Tao.Infrastructure.Outbound;

public interface IOutboundResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemOutboundResolver : IOutboundResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// Resolves a host and refuses it unless every address is public: one internal record is enough
/// to refuse (DNS rebinding). The returned address is the one the socket must connect to.
/// </summary>
public sealed class OutboundGuard(IOutboundResolver resolver)
{
    public async Task<IPAddress> ResolveAllowedAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return OutboundPolicy.IsPublicAddress(literal) ? literal : throw new OutboundBlockedException("address_not_allowed");
        IPAddress[] addresses;
        try { addresses = await resolver.ResolveAsync(host, ct); }
        catch (SocketException) { throw new OutboundBlockedException("address_not_allowed"); }
        if (addresses.Length == 0 || addresses.Any(a => !OutboundPolicy.IsPublicAddress(a)))
            throw new OutboundBlockedException("address_not_allowed");
        return addresses[0];
    }
}

public sealed record OutboundRequest(HttpMethod Method, string Url, IReadOnlyDictionary<string, string>? Headers = null,
    string? JsonBody = null, byte[]? BinaryBody = null);

public sealed record OutboundResponse(int Status, string Body, string? ContentType);

/// <summary>
/// The only HTTP client for addresses chosen by the user: public HTTPS addresses, no proxy, no
/// cookies, at most three redirects and only inside the same host, short timeouts and a body cap.
/// </summary>
public sealed class OutboundClient
{
    public const int DefaultMaxBytes = 1_048_576;
    private readonly HttpClient _http;

    public OutboundClient(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };

    /// <summary>Production client: the socket is opened only to an address the guard accepted.</summary>
    public static OutboundClient Create(OutboundGuard guard, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, ct) =>
            {
                var address = await guard.ResolveAllowedAsync(context.DnsEndPoint.Host, ct);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new OutboundClient(handler, timeout);
    }

    public async Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken ct, int maxBytes = DefaultMaxBytes)
    {
        var uri = OutboundPolicy.RequireHttpsUrl(request.Url);
        var origin = uri;
        var method = request.Method;
        var body = request.JsonBody;
        for (var hop = 0; ; hop++)
        {
            using var message = new HttpRequestMessage(method, uri);
            if (body is not null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            else if (request.BinaryBody is not null && method != HttpMethod.Get) message.Content = new ByteArrayContent(request.BinaryBody);
            foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
                if (!message.Headers.TryAddWithoutValidation(name, value))
                    message.Content?.Headers.TryAddWithoutValidation(name, value);

            using var response = await SendOnceAsync(message, ct);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= OutboundPolicy.MaxRedirects || response.Headers.Location is not { } location)
                    throw new OutboundBlockedException("redirect_not_allowed");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(next.Host, origin.Host, StringComparison.OrdinalIgnoreCase) || next.Port != origin.Port)
                    throw new OutboundBlockedException("redirect_not_allowed");
                if (status is 301 or 302 or 303) { method = HttpMethod.Get; body = null; }
                uri = next;
                continue;
            }
            return new OutboundResponse(status, await ReadCappedAsync(response, maxBytes, ct),
                response.Content.Headers.ContentType?.MediaType);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try { return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) when (Blocked(e) is { } blocked) { throw blocked; }
        catch (HttpRequestException) { throw new OutboundBlockedException("unreachable"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new OutboundBlockedException("timeout"); }
    }

    private static OutboundBlockedException? Blocked(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
            if (inner is OutboundBlockedException blocked) return blocked;
        return null;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new OutboundBlockedException("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new OutboundBlockedException("response_too_large");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
