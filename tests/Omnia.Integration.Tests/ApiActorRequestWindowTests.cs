using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Synthetic quota contract. Real PostgreSQL WebApplicationFactory covers the
/// on-server pipeline; OIDC/JWKS is independently verified in browser E2E.
/// </summary>
public sealed class ApiActorRequestWindowTests
{
    [Fact]
    public void ActorRateConfigurationAndKeyLengthFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => new ApiActorRequestWindowLimiter(0));
        Assert.Throws<InvalidOperationException>(() => new ApiActorRequestWindowLimiter(1001));
        Assert.Throws<InvalidOperationException>(() => new ApiActorRequestWindowLimiter(10, 0));
        Assert.Throws<InvalidOperationException>(() => new ApiActorRequestWindowLimiter(10, 32769));

        var limiter = new ApiActorRequestWindowLimiter();
        Assert.Equal(120, limiter.RequestsPerMinute);
        Assert.Equal(8192, limiter.MaxTrackedActors);
        Assert.Equal(0, limiter.TrackedActorPartitions);
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(" ", "tenant:fixture", out _));
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire(new string('x', 513), "tenant:fixture", out _));
        Assert.Equal(0, limiter.TrackedActorPartitions);
    }

    [Fact]
    public void ActorWindowUsesDeterministic60SecondsAndRetryAfter()
    {
        var clock = new ManualClock();
        var limiter = new ApiActorRequestWindowLimiter(2, 3, clock);
        Assert.True(limiter.TryAcquire("account:one", "tenant:fixture", out var retry));
        Assert.Equal(0, retry);
        Assert.True(limiter.TryAcquire("account:one", "tenant:fixture", out retry));
        Assert.False(limiter.TryAcquire("account:one", "tenant:fixture", out retry));
        Assert.Equal(60, retry);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAcquire("account:one", "tenant:fixture", out retry));
        Assert.Equal(1, retry);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("account:one", "tenant:fixture", out retry));
        Assert.Equal(0, retry);
        Assert.Equal(1, limiter.TrackedActorPartitions);
    }

    [Fact]
    public void NewActorsFailClosedAtCapacityButActiveActorsKeepUnusedQuota()
    {
        var clock = new ManualClock();
        var limiter = new ApiActorRequestWindowLimiter(3, 2, clock);
        Assert.True(limiter.TryAcquire("account:first", "tenant:fixture", out _));
        Assert.True(limiter.TryAcquire("account:second", "tenant:fixture", out _));
        Assert.False(limiter.TryAcquire("account:third", "tenant:fixture", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.Equal(2, limiter.TrackedActorPartitions);
        Assert.True(limiter.TryAcquire("account:first", "tenant:fixture", out _));
        Assert.True(limiter.TryAcquire("account:first", "tenant:fixture", out _));
        Assert.False(limiter.TryAcquire("account:first", "tenant:fixture", out _));
        Assert.False(limiter.TryAcquire("account:third", "tenant:fixture", out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("account:third", "tenant:fixture", out _));
        Assert.Equal(1, limiter.TrackedActorPartitions);
        Assert.True(limiter.TryAcquire("account:first", "tenant:fixture", out _));
    }

    [Fact]
    public void ThousandExpiredActorsAreReclaimedWithoutUnboundedCardinality()
    {
        var clock = new ManualClock();
        var limiter = new ApiActorRequestWindowLimiter(1, 2, clock);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(limiter.TryAcquire($"account:test:{i}", "tenant:fixture", out _));
            Assert.InRange(limiter.TrackedActorPartitions, 1, 2);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.True(limiter.TryAcquire("account:last", "tenant:fixture", out _));
        Assert.Equal(1, limiter.TrackedActorPartitions);
    }

    [Fact]
    public async Task ParallelActorAdmissionsDoNotOvergrantMinuteBudget()
    {
        var limiter = new ApiActorRequestWindowLimiter(7, 2, new ManualClock());
        var admitted = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(i => Task.Run(() => limiter.TryAcquire("account:first", "tenant:fixture", out _))));
        Assert.Equal(7, admitted.Count(x => x));
        Assert.Equal(1, limiter.TrackedActorPartitions);
        Assert.False(limiter.TryAcquire("account:first", "tenant:fixture", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.True(limiter.TryAcquire("account:second", "tenant:fixture", out _));
        Assert.False(limiter.TryAcquire("account:third", "tenant:fixture", out _));
    }

    [Fact]
    public async Task Actor429IsBeforeBodyReadAndNeverRevealsSignedIdentity()
    {
        var clock = new ManualClock();
        var limiter = new ApiAtomicRequestWindowLimiter(
            new ApiActorRequestWindowLimiter(1, 4, clock),
            new ApiTenantRequestWindowLimiter(10000, 16, clock));
        var first = Context("tenant-alpha", "actor-one", Stream.Null);
        var firstRan = false;
        await ApiAtomicRequestRateBudget.InvokeAsync(first, _ =>
        {
            firstRan = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(firstRan);

        using var failRead = new FailOnReadStream();
        var rejected = Context("tenant-alpha", "actor-one", failRead);
        rejected.Request.Method = HttpMethods.Post;
        rejected.Request.Headers["X-Tenant-Id"] = "tenant-beta";
        rejected.Request.Headers["X-Forwarded-For"] = "203.0.113.34";
        var handlerCalled = false;
        await ApiAtomicRequestRateBudget.InvokeAsync(rejected, _ =>
        {
            handlerCalled = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.False(handlerCalled);
        Assert.Equal(0, failRead.ReadCalls);
        Assert.Same(failRead, rejected.Request.Body);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal("no-store", rejected.Response.Headers.CacheControl.ToString());
        Assert.Equal("60", rejected.Response.Headers["Retry-After"].ToString());
        rejected.Response.Body.Position = 0;
        using (var reader = new StreamReader(rejected.Response.Body,
            Encoding.UTF8, leaveOpen: true))
        {
            var response = await reader.ReadToEndAsync();
            Assert.Contains("rate_limited", response);
            Assert.DoesNotContain("tenant-alpha", response);
            Assert.DoesNotContain("actor-one", response);
        }

        // Different subject in the SAME tenant is a distinct actor.
        var differentSubject = Context("tenant-alpha", "actor-two", Stream.Null);
        var called = false;
        await ApiAtomicRequestRateBudget.InvokeAsync(differentSubject, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(called);

        // Identical sub in another tenant has a different signed partition.
        var otherTenant = Context("tenant-beta", "actor-one", Stream.Null);
        called = false;
        await ApiAtomicRequestRateBudget.InvokeAsync(otherTenant, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(called);

        var health = Context("tenant-alpha", "actor-one", Stream.Null);
        health.Request.Path = "/health";
        called = false;
        await ApiAtomicRequestRateBudget.InvokeAsync(health, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, health.Response.StatusCode);
    }


    [Fact]
    public void SignedTenantActorPartitionsAreBoundedAndReclaimedAfterExpiry()
    {
        var clock = new ManualClock();
        var limiter = new ApiActorRequestWindowLimiter(
            5, 3, clock, maxTrackedActorsPerTenant: 1);
        Assert.Equal(1, limiter.MaxTrackedActorsPerTenant);
        Assert.True(limiter.TryAcquire("account:a1", "tenant:A", out _));
        Assert.False(limiter.TryAcquire("account:a2", "tenant:A", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.Equal(1, limiter.TrackedActorPartitions);
        Assert.True(limiter.TryAcquire("account:a1", "tenant:A", out _));
        Assert.True(limiter.TryAcquire("account:b1", "tenant:B", out _));
        Assert.True(limiter.TryAcquire("account:c1", "tenant:C", out _));
        Assert.Equal(3, limiter.TrackedActorPartitions);
        Assert.False(limiter.TryAcquire("account:d1", "tenant:D", out _));

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(limiter.TryAcquire("account:a2", "tenant:A", out _));
        Assert.Equal(1, limiter.TrackedActorPartitions);
        Assert.True(limiter.TryAcquire("account:b2", "tenant:B", out _));
    }

    [Fact]
    public async Task ConcurrentNewSubjectsCannotExceedOneTenantsPartitionCap()
    {
        var limiter = new ApiActorRequestWindowLimiter(
            10, 4, new ManualClock(), maxTrackedActorsPerTenant: 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(i => Task.Run(() =>
                limiter.TryAcquire("account:" + i, "tenant:A", out _))));
        Assert.Equal(2, results.Count(x => x));
        Assert.Equal(2, limiter.TrackedActorPartitions);
        Assert.True(limiter.TryAcquire("account:other", "tenant:B", out _));
        Assert.Equal(3, limiter.TrackedActorPartitions);
    }

    [Fact]
    public void PerTenantActorCardinalityMustRejectUnsafeConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ApiActorRequestWindowLimiter(10, 3, maxTrackedActorsPerTenant: 0));
        Assert.Throws<InvalidOperationException>(() =>
            new ApiActorRequestWindowLimiter(10, 3, maxTrackedActorsPerTenant: 4));
        var limiter = new ApiActorRequestWindowLimiter(10, 3);
        Assert.Equal(3, limiter.MaxTrackedActorsPerTenant);
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire("account:a", " ", out _));
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire("account:a", new string('t', 257), out _));
        Assert.Equal(0, limiter.TrackedActorPartitions);
    }

    private static DefaultHttpContext Context(string tenant, string actor, Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/consents";
        context.Request.Method = HttpMethods.Get;
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", actor)],
            "synthetic-test"));
        return context;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class FailOnReadStream : Stream
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            throw new InvalidOperationException("Rejected request body must not be read.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("Rejected request body must not be read.");
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
