using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Direct Kestrel transport tests, not TestServer. Loopback synthetic HTTP only:
/// no JWT, personal data, DB, external network, TLS or production claims.
/// </summary>
public sealed class ApiKestrelTransportLimitsTests
{
    [Theory]
    [InlineData(0, 16384, 64)]
    [InlineData(999, 16384, 64)]
    [InlineData(30001, 16384, 64)]
    [InlineData(10000, 8191, 64)]
    [InlineData(10000, 32769, 64)]
    [InlineData(10000, 16384, 15)]
    [InlineData(10000, 16384, 101)]
    public void RejectsUnsafeTransportConfigurationBeforeBindingSockets(
        int headerTimeoutMilliseconds, int headerBytes, int headerCount)
    {
        var limits = new KestrelServerLimits();
        Assert.Throws<InvalidOperationException>(() =>
            ApiKestrelTransportLimits.Configure(
                limits, headerTimeoutMilliseconds, headerBytes, headerCount));
    }

    [Theory]
    [InlineData(1000, 8192, 16)]
    [InlineData(10000, 16384, 64)]
    [InlineData(30000, 32768, 100)]
    public void AppliesExactServerLimitsAndDoesNotDisableBodyRate(
        int headerTimeoutMilliseconds, int headerBytes, int headerCount)
    {
        var limits = new KestrelServerLimits();
        ApiKestrelTransportLimits.Configure(
            limits, headerTimeoutMilliseconds, headerBytes, headerCount);
        Assert.Equal(TimeSpan.FromMilliseconds(headerTimeoutMilliseconds),
            limits.RequestHeadersTimeout);
        Assert.Equal(headerBytes, limits.MaxRequestHeadersTotalSize);
        Assert.Equal(headerCount, limits.MaxRequestHeaderCount);
        Assert.Equal(70 * 1024, limits.MaxRequestBodySize);

        // Keep the built-in Kestrel minimum rate explicitly enabled.
        Assert.NotNull(limits.MinRequestBodyDataRate);
        Assert.Equal(240, limits.MinRequestBodyDataRate.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(5),
            limits.MinRequestBodyDataRate.GracePeriod);
    }

    [Fact]
    public async Task ActualLoopbackKestrelDropsIncompleteHeadersButAllowsNormalGet()
    {
        // A REAL Kestrel socket is needed: TestServer skips header parsing.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            Args = []
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            ApiKestrelTransportLimits.Configure(
                options.Limits, 1000, 16384, 64);
            options.Listen(IPAddress.Loopback, 0);
        });
        await using var app = builder.Build();
        var handlerCalls = 0;
        app.MapGet("/probe", () =>
        {
            Interlocked.Increment(ref handlerCalls);
            return Results.Text("synthetic-ok");
        });
        await app.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var url = Assert.Single(addresses!.Addresses);
            var port = new Uri(url).Port;

            using (var client = new HttpClient())
            using (var success = await client.GetAsync(url + "/probe"))
            {
                Assert.Equal(HttpStatusCode.OK, success.StatusCode);
                Assert.Equal("synthetic-ok",
                    await success.Content.ReadAsStringAsync());
            }

            using var slowClient = new TcpClient();
            await slowClient.ConnectAsync(IPAddress.Loopback, port);
            using var slowStream = slowClient.GetStream();
            // Deliberately NEVER finish the header block with CRLFCRLF.
            await slowStream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /probe HTTP/1.1\r\nHost: localhost\r\nX-Partial:"));
            await slowStream.FlushAsync();

            var buffer = new byte[2048];
            int received;
            try
            {
                received = await slowStream.ReadAsync(buffer)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(7));
            }
            catch (IOException)
            {
                // A reset is also a correct transport-level timeout.
                received = 0;
            }

            var response = Encoding.ASCII.GetString(buffer, 0, received);
            Assert.True(received == 0 ||
                response.StartsWith("HTTP/1.1 408", StringComparison.Ordinal),
                "Incomplete headers must be timed out/closed, not dispatched.");
            Assert.Equal(1, Volatile.Read(ref handlerCalls));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
