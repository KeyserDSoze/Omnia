using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Deterministic synthetic fixed-window contracts. Browser OIDC and real
/// PostgreSQL endpoint rate limits are verified by separate integration/E2E.
/// No real identity, network traffic or personal content.
/// </summary>
public sealed class ApiTenantRequestWindowTests
{
    [Fact]
    public void QuotaAndPartitionCapacityHaveStrictBounds()
    {
        Assert.Throws<InvalidOperationException>(() => new ApiTenantRequestWindowLimiter(0));
        Assert.Throws<InvalidOperationException>(() => new ApiTenantRequestWindowLimiter(10001));
        Assert.Throws<InvalidOperationException>(() => new ApiTenantRequestWindowLimiter(2, 0));
        Assert.Throws<InvalidOperationException>(() => new ApiTenantRequestWindowLimiter(2, 16385));
        var limiter = new ApiTenantRequestWindowLimiter();
        Assert.Equal(600, limiter.TenantRequestsPerMinute);
        Assert.Equal(4096, limiter.MaxTrackedTenants);
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(" ", out _));
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire(new string('x', 257), out _));
    }

    [Fact]
    public void AggregateTenantWindowExhaustsAndRenewsAfterSixtySeconds()
    {
        var clock = new ManualClock();
        var limiter = new ApiTenantRequestWindowLimiter(2, 4, clock);
        Assert.True(limiter.TryAcquire("tenant:a", out var retry));
        Assert.Equal(0, retry);
        Assert.True(limiter.TryAcquire("tenant:a", out retry));
        Assert.Equal(0, retry);
        Assert.False(limiter.TryAcquire("tenant:a", out retry));
        Assert.Equal(60, retry);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAcquire("tenant:a", out retry));
        Assert.Equal(1, retry);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("tenant:a", out retry));
        Assert.Equal(0, retry);
        Assert.Equal(1, limiter.TrackedTenantPartitions);
        Assert.True(limiter.TryAcquire("tenant:other", out retry));
        Assert.Equal(2, limiter.TrackedTenantPartitions);
    }

    [Fact]
    public void NewTenantsFailClosedWhenPartitionCapacityIsFullAndIdleKeysExpire()
    {
        var clock = new ManualClock();
        var limiter = new ApiTenantRequestWindowLimiter(3, 2, clock);
        Assert.True(limiter.TryAcquire("tenant:a", out _));
        Assert.True(limiter.TryAcquire("tenant:b", out _));
        Assert.False(limiter.TryAcquire("tenant:c", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.Equal(2, limiter.TrackedTenantPartitions);

        // An existing active tenant may still spend unused capacity even
        // when no more distinct partitions can be admitted.
        Assert.True(limiter.TryAcquire("tenant:b", out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("tenant:c", out _));
        Assert.Equal(1, limiter.TrackedTenantPartitions);
        Assert.True(limiter.TryAcquire("tenant:a", out _));
    }

    [Fact]
    public void ThousandsOfShortLivedTenantsDoNotAccumulateWithoutBound()
    {
        var clock = new ManualClock();
        var limiter = new ApiTenantRequestWindowLimiter(1, 2, clock);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(limiter.TryAcquire($"tenant:synthetic-{i}", out _));
            Assert.InRange(limiter.TrackedTenantPartitions, 1, 2);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.True(limiter.TryAcquire("tenant:last", out _));
        Assert.Equal(1, limiter.TrackedTenantPartitions);
    }

    [Fact]
    public async Task ParallelTenantAdmissionsNeverExceedSharedMinuteLimit()
    {
        var limiter = new ApiTenantRequestWindowLimiter(7, 2, new ManualClock());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(i => Task.Run(() => limiter.TryAcquire("tenant:a", out _))));
        Assert.Equal(7, attempts.Count(admitted => admitted));
        Assert.Equal(1, limiter.TrackedTenantPartitions);
        Assert.False(limiter.TryAcquire("tenant:a", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.True(limiter.TryAcquire("tenant:b", out _));
        Assert.False(limiter.TryAcquire("tenant:c", out _));
    }

    [Fact]
    public async Task Tenant429ShortCircuitsBeforeBodyReadWithoutRevealingIdentity()
    {
        var limiter = new ApiTenantRequestWindowLimiter(1, 4, new ManualClock());
        var first = Request("/api/v1/consents", "tenant-one", "actor-one", Stream.Null);
        var firstHandlerCalled = false;
        await ApiTenantRequestRateBudget.InvokeAsync(first, _ =>
        {
            firstHandlerCalled = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(firstHandlerCalled);

        using var forbiddenRead = new FailOnReadStream();
        var rejected = Request("/api/v1/memories", "tenant-one", "actor-two", forbiddenRead);
        rejected.Request.Method = HttpMethods.Post;
        rejected.Request.Headers["X-Tenant-Id"] = "tenant-other";
        rejected.Request.Headers["X-Forwarded-For"] = "203.0.113.4";
        var handlerCalled = false;
        await ApiTenantRequestRateBudget.InvokeAsync(rejected, _ =>
        {
            handlerCalled = true;
            return Task.CompletedTask;
        }, limiter);

        Assert.False(handlerCalled);
        Assert.Equal(0, forbiddenRead.ReadCalls);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal("no-store", rejected.Response.Headers.CacheControl.ToString());
        Assert.Equal("60", rejected.Response.Headers["Retry-After"].ToString());
        rejected.Response.Body.Position = 0;
        using var reader = new StreamReader(rejected.Response.Body, Encoding.UTF8, leaveOpen: true);
        var error = await reader.ReadToEndAsync();
        Assert.Contains("rate_limited", error);
        Assert.DoesNotContain("tenant-one", error);
        Assert.DoesNotContain("actor-two", error);

        var health = Request("/health", "tenant-one", "actor-two", Stream.Null);
        var healthCalled = false;
        await ApiTenantRequestRateBudget.InvokeAsync(health, _ =>
        {
            healthCalled = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(healthCalled);
        Assert.Equal(StatusCodes.Status200OK, health.Response.StatusCode);

        var separateTenant = Request("/api/v1/consents", "tenant-other", "actor-two",
            Stream.Null);
        var otherCalled = false;
        await ApiTenantRequestRateBudget.InvokeAsync(separateTenant, _ =>
        {
            otherCalled = true;
            return Task.CompletedTask;
        }, limiter);
        Assert.True(otherCalled);
    }

    private static DefaultHttpContext Request(
        string path, string tenant, string subject, Stream body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = path;
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = body;
        ctx.Response.Body = new MemoryStream();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", subject)],
            "synthetic-test"));
        return ctx;
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
            throw new InvalidOperationException("body must not be read");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("body must not be read");
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
