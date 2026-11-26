using System.Net;
using System.Text;
using Tao.Domain;
using Tao.Infrastructure.Outbound;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class OutboundGuardTests
{
    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void PublicAddressesAreAllowed(string address) =>
        Assert.True(OutboundPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    public void InternalAddressesAreRefused(string address) =>
        Assert.False(OutboundPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://mcp.example.com/mcp")]
    [InlineData("https://api.example.com:8443/v1/tools?x=1")]
    [InlineData("https://93.184.216.34/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    public void PublicHttpsAddressesAreAccepted(string url) =>
        Assert.NotNull(OutboundPolicy.RequireHttpsUrl(url));

    [Theory]
    [InlineData(null, "url_invalid")]
    [InlineData("", "url_invalid")]
    [InlineData("not a url", "url_invalid")]
    [InlineData("/relative/path", "url_invalid")]
    [InlineData("http://mcp.example.com/", "https_required")]
    [InlineData("ftp://mcp.example.com/", "https_required")]
    [InlineData("file:///etc/passwd", "https_required")]
    [InlineData("https://user:secret@mcp.example.com/", "url_invalid")]
    [InlineData("https://localhost/", "address_not_allowed")]
    [InlineData("https://LOCALHOST./", "address_not_allowed")]
    [InlineData("https://app.localhost/", "address_not_allowed")]
    [InlineData("https://printer.local/", "address_not_allowed")]
    [InlineData("https://db.internal/", "address_not_allowed")]
    [InlineData("https://intranet/", "address_not_allowed")]
    [InlineData("https://metadata/", "address_not_allowed")]
    [InlineData("https://127.0.0.1/", "address_not_allowed")]
    [InlineData("https://127.1/", "address_not_allowed")]
    [InlineData("https://2130706433/", "address_not_allowed")]
    [InlineData("https://0x7f000001/", "address_not_allowed")]
    [InlineData("https://0177.0.0.1/", "address_not_allowed")]
    [InlineData("https://169.254.169.254/metadata/identity/oauth2/token", "address_not_allowed")]
    [InlineData("https://[::1]/", "address_not_allowed")]
    [InlineData("https://[::ffff:127.0.0.1]/", "address_not_allowed")]
    [InlineData("https://[fd00:ec2::254]/", "address_not_allowed")]
    public void UnsafeAddressesAreRefusedWithAStableCode(string? url, string code) =>
        Assert.Equal(code, Assert.Throws<OutboundBlockedException>(() => OutboundPolicy.RequireHttpsUrl(url)).Code);

    [Fact]
    public async Task ARecordSetWithAnyInternalAddressIsRefused()
    {
        var guard = new OutboundGuard(new FakeResolver(["93.184.216.34", "10.0.0.5"]));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            guard.ResolveAllowedAsync("api.example.test", default));
        Assert.Equal("address_not_allowed", error.Code);
    }

    [Fact]
    public async Task APublicRecordSetIsResolvedToOneAddress()
    {
        var guard = new OutboundGuard(new FakeResolver(["93.184.216.34"]));
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await guard.ResolveAllowedAsync("api.example.test", default));
    }

    [Fact]
    public async Task AnUnresolvableHostIsRefused()
    {
        var guard = new OutboundGuard(new FakeResolver([]));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            guard.ResolveAllowedAsync("nowhere.example.test", default));
        Assert.Equal("address_not_allowed", error.Code);
    }

    [Fact]
    public async Task ARealConnectionToAnInternalAddressIsNeverMade()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // The name looks public but resolves to loopback: the connect step must refuse it.
        var client = OutboundClient.Create(new OutboundGuard(new FakeResolver(["127.0.0.1"])));

        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, $"https://rebind.example.test:{port}/"), default));

        Assert.Equal("address_not_allowed", error.Code);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ARedirectToAnotherHostIsRefused()
    {
        var client = new OutboundClient(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "https://other.example.test/x")));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://api.example.test/start"), default));
        Assert.Equal("redirect_not_allowed", error.Code);
    }

    [Fact]
    public async Task ARedirectDowngradingToHttpIsRefused()
    {
        var client = new OutboundClient(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "http://api.example.test/x")));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://api.example.test/start"), default));
        Assert.Equal("redirect_not_allowed", error.Code);
    }

    [Fact]
    public async Task ASameHostRedirectIsFollowedAndTheAuthorizationHeaderStays()
    {
        var seen = new List<(Uri Uri, string? Authorization)>();
        var client = new OutboundClient(new ScriptedHandler(request =>
        {
            seen.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return request.RequestUri!.AbsolutePath == "/start"
                ? Redirect(HttpStatusCode.TemporaryRedirect, "/final")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
        }));

        var response = await client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://api.example.test/start",
            new Dictionary<string, string> { ["Authorization"] = "Bearer t" }), default);

        Assert.Equal((200, "{\"ok\":true}"), (response.Status, response.Body));
        Assert.Equal(["/start", "/final"], seen.Select(s => s.Uri.AbsolutePath));
        Assert.All(seen, s => Assert.Equal("Bearer t", s.Authorization));
    }

    [Fact]
    public async Task TooManyRedirectsAreRefused()
    {
        var client = new OutboundClient(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "/again")));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://api.example.test/start"), default));
        Assert.Equal("redirect_not_allowed", error.Code);
    }

    [Fact]
    public async Task AResponseOverTheLimitIsRefused()
    {
        var client = new OutboundClient(new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) }));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://api.example.test/big"), default, maxBytes: 1000));
        Assert.Equal("response_too_large", error.Code);
    }

    [Fact]
    public async Task AnUnsafeAddressIsRefusedBeforeAnyRequest()
    {
        var calls = 0;
        var client = new OutboundClient(new ScriptedHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var error = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            client.SendAsync(new OutboundRequest(HttpMethod.Get, "https://169.254.169.254/latest/meta-data"), default));
        Assert.Equal(("address_not_allowed", 0), (error.Code, calls));
    }

    [Fact]
    public async Task APostSendsItsJsonBody()
    {
        string? body = null;
        var client = new OutboundClient(new ScriptedHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }));
        var response = await client.SendAsync(new OutboundRequest(HttpMethod.Post, "https://api.example.test/rpc", null,
            "{\"jsonrpc\":\"2.0\"}"), default);
        Assert.Equal(("ok", "{\"jsonrpc\":\"2.0\"}"), (response.Body, body));
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeResolver(string[] addresses) : IOutboundResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
