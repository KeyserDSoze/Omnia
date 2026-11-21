using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Test-first synthetic tenant isolation contract for JSON buffers that stay
/// alive during slow endpoint processing. Real OIDC/JWKS is tested separately.
/// </summary>
public sealed class ApiJsonBufferedTenantFairnessTests
{
    [Fact]
    public async Task ManyCompletedBodiesFromOneTenantCannotOccupyAllBufferedRequestSlots()
    {
        // Existing #163 global=32. All reads finish immediately, then 16
        // downstream handlers remain blocked with a live bounded buffer.
        // A seventeenth distinct signed actor of the same tenant must NOT
        // receive a slot even while half the global slots are still free.
        var bufferedLimiter = new ApiJsonBufferedRequestLimiter(32);
        var readerLimiter = new ApiJsonBodyReadConcurrencyLimiter(32);
        var releaseHandlers = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new List<(MemoryStream Body, Task Running)>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
                var context = Request(body, "tenant-a", $"actor-{i}");
                var enteredHandler = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var running = ApiJsonBodyBudget.InvokeAsync(context, async _ =>
                {
                    enteredHandler.TrySetResult(true);
                    await releaseHandlers.Task;
                }, 1024, 5000, readerLimiter, bufferedLimiter);
                held.Add((body, running));
                await enteredHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            using var rejectedBody = new MustNotReadStream();
            var rejected = Request(rejectedBody, "tenant-a", "actor-seventeen");
            rejected.Request.Headers["X-Tenant-Id"] = "tenant-b";
            rejected.Request.Headers["X-Forwarded-For"] = "198.51.100.79";
            var rejectedHandler = false;
            await ApiJsonBodyBudget.InvokeAsync(rejected, _ =>
            {
                rejectedHandler = true;
                return Task.CompletedTask;
            }, 1024, 5000, readerLimiter, bufferedLimiter);

            Assert.False(rejectedHandler);
            Assert.Equal(0, rejectedBody.ReadCount);
            Assert.Same(rejectedBody, rejected.Request.Body);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, rejected.Response.StatusCode);
            Assert.Equal("no-store", rejected.Response.Headers.CacheControl.ToString());
            Assert.Equal("1", rejected.Response.Headers["Retry-After"].ToString());
            var errorText = await ResponseTextAsync(rejected);
            Assert.Contains("request_buffer_busy", errorText);
            Assert.DoesNotContain("tenant-a", errorText);
            Assert.DoesNotContain("actor-seventeen", errorText);

            // A different tenant can use spare process-wide capacity,
            // even when tenant-a holds sixteen completed JSON buffers.
            await AssertAcceptedAsync(bufferedLimiter, readerLimiter,
                "tenant-b", "actor-seventeen");
            await AssertAcceptedAsync(bufferedLimiter, readerLimiter,
                "tenant-b", "actor-0");
        }
        finally
        {
            releaseHandlers.TrySetResult(true);
            await Task.WhenAll(held.Select(x => x.Running));
            foreach (var item in held) item.Body.Dispose();
        }

        await AssertAcceptedAsync(bufferedLimiter, readerLimiter,
            "tenant-a", "actor-seventeen");
    }

    private static async Task AssertAcceptedAsync(
        ApiJsonBufferedRequestLimiter bufferLimiter,
        ApiJsonBodyReadConcurrencyLimiter readerLimiter,
        string tenant, string sub)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var context = Request(bytes, tenant, sub);
        var called = false;
        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, 1024, 5000, readerLimiter, bufferLimiter);
        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Same(bytes, context.Request.Body);
    }

    private static DefaultHttpContext Request(Stream bytes, string tenant, string sub)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = "application/json";
        context.Request.Body = bytes;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", sub)],
            "synthetic-test"));
        return context;
    }

    private static async Task<string> ResponseTextAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class MustNotReadStream : Stream
    {
        public int ReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("Rejected body must not be read");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Rejected body must not be read");
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
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
