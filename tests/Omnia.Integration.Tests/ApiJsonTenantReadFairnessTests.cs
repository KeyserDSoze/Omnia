using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Test-first behavioral contract for per-tenant JSON intake admission.
/// No real tenant data, external services or authentication shortcut in API.
/// Real JWKS/OIDC verification remains in the separate browser E2E suite.
/// </summary>
public sealed class ApiJsonTenantReadFairnessTests
{
    [Fact]
    public async Task ManyActorsInOneTenantMustNotUseEveryGlobalBodyReadSlot()
    {
        // Existing process cap=32, actor cap=4. Without a tenant budget,
        // 16 held reads across four actors allow a fifth actor to consume
        // yet another slot, crowding out a different tenant.
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(32);
        var held = new List<(BlockingReadStream Stream, Task Operation)>();
        try
        {
            for (var actor = 0; actor < 4; actor++)
            {
                for (var read = 0; read < 4; read++)
                {
                    var stream = new BlockingReadStream();
                    var request = Request(stream, "tenant-one", $"actor-{actor}");
                    var operation = ApiJsonBodyBudget.InvokeAsync(
                        request, _ => Task.CompletedTask, 1024, 20_000, limiter);
                    held.Add((stream, operation));
                    await stream.WaitForReadAsync();
                }
            }

            // Not the same actor: the existing per-actor cap is not enough.
            using var forbiddenBody = new MemoryStream(
                Encoding.UTF8.GetBytes("{\"synthetic\":\"not-for-logs\"}"));
            var forbidden = Request(forbiddenBody, "tenant-one", "actor-five");
            // Forged forwarding and tenant headers must not select another
            // quota partition; signed context claims remain authoritative.
            forbidden.Request.Headers["X-Forwarded-For"] = "198.51.100.20";
            forbidden.Request.Headers["X-Tenant-Id"] = "tenant-two";
            var forbiddenHandler = false;
            await ApiJsonBodyBudget.InvokeAsync(forbidden, _ =>
            {
                forbiddenHandler = true;
                return Task.CompletedTask;
            }, 1024, 20_000, limiter);

            Assert.False(forbiddenHandler);
            Assert.Equal(0, forbiddenBody.Position);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, forbidden.Response.StatusCode);
            Assert.Equal("no-store", forbidden.Response.Headers.CacheControl.ToString());
            Assert.Equal("1", forbidden.Response.Headers["Retry-After"].ToString());
            var rejection = await ResponseTextAsync(forbidden);
            Assert.Contains("request_body_busy", rejection);
            Assert.DoesNotContain("tenant-one", rejection);
            Assert.DoesNotContain("actor-five", rejection);
            Assert.DoesNotContain("not-for-logs", rejection);

            // This tenant must retain access to the remaining global
            // slots: the policy is an upper bound, not hard reservations.
            await AssertAcceptedAsync(limiter, "tenant-two", "actor-five");
            await AssertAcceptedAsync(limiter, "tenant-two", "actor-0");
        }
        finally
        {
            foreach (var item in held)
                item.Stream.Complete();
            await Task.WhenAll(held.Select(item => item.Operation));
            foreach (var item in held)
                item.Stream.Dispose();
        }

        // After held requests complete, the first tenant is usable again.
        await AssertAcceptedAsync(limiter, "tenant-one", "actor-five");
    }

    private static async Task AssertAcceptedAsync(
        ApiJsonBodyReadConcurrencyLimiter limiter, string tenant, string subject)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var context = Request(stream, tenant, subject);
        var reached = false;
        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, 1024, 20_000, limiter);
        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Same(stream, context.Request.Body);
    }

    private static DefaultHttpContext Request(
        Stream body, string tenant, string subject)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", subject)],
            "synthetic-fixture"));
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    private static async Task<string> ResponseTextAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForReadAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Complete() => _done.TrySetResult(true);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadCoreAsync(cancellationToken));

        private async Task<int> ReadCoreAsync(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            await _done.Task.WaitAsync(ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
