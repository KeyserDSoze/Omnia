using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Synthetic authenticated actor fairness: no database, private data or
/// real OIDC shortcuts; genuine JWT validation is tested separately.
/// </summary>
public sealed class ApiJsonActorReadFairnessTests
{
    [Fact]
    public void ActorLimitsAreValidatedAndActivePartitionKeysDoNotAccumulate()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBodyReadConcurrencyLimiter(8, 0));
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBodyReadConcurrencyLimiter(8, 9));
        Assert.Throws<ArgumentException>(() =>
            new ApiJsonBodyReadConcurrencyLimiter(8).TryAcquire(new string('x', 513), "test-tenant"));

        Assert.Equal(4, new ApiJsonBodyReadConcurrencyLimiter(8).MaxConcurrentReadsPerActor);
        Assert.Equal(1, new ApiJsonBodyReadConcurrencyLimiter(1).MaxConcurrentReadsPerActor);
        Assert.Equal(2, new ApiJsonBodyReadConcurrencyLimiter(8, 2).MaxConcurrentReadsPerActor);

        var limiter = new ApiJsonBodyReadConcurrencyLimiter(8, 2);
        for (var index = 0; index < 1000; index++)
        {
            using (var lease = limiter.TryAcquire($"synthetic-account:{index}", "test-tenant"))
            {
                Assert.NotNull(lease);
                Assert.Equal(1, limiter.ActiveActorPartitions);
            }
            // No ever-growing per-subject limiter dictionary after release.
            Assert.Equal(0, limiter.ActiveActorPartitions);
        }
    }

    [Fact]
    public async Task ParallelAdmissionsAreAtomicAcrossActorAndGlobalBudgets()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(8, 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(_ => Task.Run(() => limiter.TryAcquire("signed:tenant:actor-one", "signed:tenant"))));
        try
        {
            Assert.Equal(2, results.Count(lease => lease is not null));
            Assert.Equal(1, limiter.ActiveActorPartitions);
            var other = new List<IDisposable>();
            try
            {
                // Six more active slots across three other accounts are
                // allowed, but a ninth process-wide permit must be refused.
                foreach (var actor in new[] { "actor-two", "actor-three", "actor-four" })
                {
                    for (var i = 0; i < 2; i++)
                        other.Add(Assert.IsAssignableFrom<IDisposable>(
                            limiter.TryAcquire($"signed:tenant:{actor}", "signed:tenant")));
                }

                Assert.Equal(4, limiter.ActiveActorPartitions);
                Assert.Null(limiter.TryAcquire("signed:tenant:actor-five", "signed:tenant"));
            }
            finally
            {
                foreach (var lease in other)
                    lease.Dispose();
            }
        }
        finally
        {
            foreach (var lease in results.Where(x => x is not null))
            {
                lease!.Dispose();
                lease.Dispose(); // Idempotent even after the first release.
            }
        }
        Assert.Equal(0, limiter.ActiveActorPartitions);
        using var reopened = limiter.TryAcquire("signed:tenant:actor-one", "signed:tenant");
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task OneAuthenticatedActorCannotConsumeEveryJsonReadSlot()
    {
        // Eight process-wide slots, but one verified actor should be able to
        // occupy at most four; a second actor and a different tenant are free
        // to use the remaining capacity.
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(8);
        var held = new List<(BlockingReadStream Stream, Task Work)>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var stream = new BlockingReadStream();
                var request = Request(stream, "tenant-one", "actor-one");
                var work = ApiJsonBodyBudget.InvokeAsync(request,
                    _ => Task.CompletedTask, 1024, 5000, limiter);
                held.Add((stream, work));
                await stream.WaitForReadAsync();
            }

            using var rejectedStream = new MemoryStream(
                Encoding.UTF8.GetBytes("{\"synthetic\":\"private\"}"));
            var rejected = Request(rejectedStream, "tenant-one", "actor-one");
            rejected.Request.Headers["X-Forwarded-For"] = "198.51.100.25";
            var called = false;
            await ApiJsonBodyBudget.InvokeAsync(rejected, _ =>
            {
                called = true;
                return Task.CompletedTask;
            }, 1024, 5000, limiter);

            Assert.False(called);
            Assert.Equal(0L, rejectedStream.Position);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, rejected.Response.StatusCode);
            Assert.Equal("1", rejected.Response.Headers["Retry-After"].ToString());
            Assert.Equal("no-store", rejected.Response.Headers.CacheControl.ToString());
            var error = await ResponseTextAsync(rejected);
            Assert.Contains("request_body_busy", error);
            Assert.DoesNotContain("actor-one", error);
            Assert.DoesNotContain("tenant-one", error);
            Assert.DoesNotContain("private", error);

            // Distinct subjects of a tenant, AND an identical subject of a
            // different tenant, have separate quotas even under saturation.
            await AssertJsonReadSucceedsAsync(limiter, "tenant-one", "actor-two");
            await AssertJsonReadSucceedsAsync(limiter, "tenant-two", "actor-one");
        }
        finally
        {
            foreach (var item in held)
                item.Stream.Complete();
            await Task.WhenAll(held.Select(item => item.Work));
            foreach (var item in held)
                item.Stream.Dispose();
        }

        // A released actor can reuse its original slots.
        await AssertJsonReadSucceedsAsync(limiter, "tenant-one", "actor-one");
    }

    private static async Task AssertJsonReadSucceedsAsync(
        ApiJsonBodyReadConcurrencyLimiter limiter,
        string tenant, string subject)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var context = Request(stream, tenant, subject);
        var reached = false;
        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, 1024, 5000, limiter);
        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Same(stream, context.Request.Body);
    }

    private static DefaultHttpContext Request(Stream body, string tenant, string subject)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", subject)],
            "synthetic-test"));
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
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
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForReadAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        public void Complete() => _release.TrySetResult(true);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadAsyncCore(cancellationToken));

        private async Task<int> ReadAsyncCore(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
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
