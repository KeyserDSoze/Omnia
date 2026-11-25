using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// TDD behavior-first: actor fairness must bound JSON buffers retained through
/// slow handlers without constraining the already released JSON receive permit.
/// Synthetic claims only; genuine JWT validation is in the OIDC browser suite.
/// </summary>
public sealed class ApiJsonBufferedActorFairnessTests
{
    [Fact]
    public async Task OneVerifiedActorCannotMonopolizeTenantBufferSlotsDuringSlowHandlers()
    {
        // Process-wide 16 retained buffers, tenant limit 8, receive cap 16.
        // A single signed actor holds four fully read bodies in handlers;
        // a fifth must be refused EVEN THOUGH four tenant buffer slots and
        // twelve process slots are otherwise available.
        var bufferLimiter = new ApiJsonBufferedRequestLimiter(16, 8);
        var receiveLimiter = new ApiJsonBodyReadConcurrencyLimiter(16);
        var finishHandlers = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new List<(MemoryStream Stream, Task Running)>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var bytes = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
                var request = Request(bytes, "tenant-alpha", "actor-one");
                var reachedHandler = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = ApiJsonBodyBudget.InvokeAsync(request, async _ =>
                {
                    reachedHandler.TrySetResult(true);
                    await finishHandlers.Task;
                }, 1024, 5000, receiveLimiter, bufferLimiter);
                held.Add((bytes, operation));
                await reachedHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Header spoofing must NOT change partition selected from signed
            // tenant_id+sub. The rejected body must not be read at all.
            using var rejectedBytes = new MemoryStream(
                Encoding.UTF8.GetBytes("{\"synthetic\":\"private-value\"}"));
            var rejected = Request(rejectedBytes, "tenant-alpha", "actor-one");
            rejected.Request.Headers["X-Forwarded-For"] = "198.51.100.33";
            rejected.Request.Headers["X-Tenant-Id"] = "tenant-beta";
            var handlerCalled = false;
            await ApiJsonBodyBudget.InvokeAsync(rejected, _ =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            }, 1024, 5000, receiveLimiter, bufferLimiter);

            Assert.False(handlerCalled);
            Assert.Equal(0L, rejectedBytes.Position);
            Assert.Same(rejectedBytes, rejected.Request.Body);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable,
                rejected.Response.StatusCode);
            Assert.Equal("no-store",
                rejected.Response.Headers.CacheControl.ToString());
            Assert.Equal("1", rejected.Response.Headers["Retry-After"].ToString());
            var error = await ErrorTextAsync(rejected);
            Assert.Contains("request_buffer_busy", error);
            Assert.DoesNotContain("tenant-alpha", error);
            Assert.DoesNotContain("actor-one", error);
            Assert.DoesNotContain("private-value", error);

            // A second subject of the SAME tenant may use the remaining
            // slots. Identical sub in a DIFFERENT tenant is independent.
            await AssertAcceptedAsync(bufferLimiter, receiveLimiter,
                "tenant-alpha", "actor-two");
            await AssertAcceptedAsync(bufferLimiter, receiveLimiter,
                "tenant-beta", "actor-one");
        }
        finally
        {
            finishHandlers.TrySetResult(true);
            await Task.WhenAll(held.Select(x => x.Running));
            foreach (var item in held)
                item.Stream.Dispose();
        }

        // After the slow handlers return, all capacity is reusable.
        await AssertAcceptedAsync(bufferLimiter, receiveLimiter,
            "tenant-alpha", "actor-one");
    }

    [Fact]
    public void ActorBufferedDefaultsConfigAndEphemeralKeysAreBounded()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBufferedRequestLimiter(16, 8, 0));
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBufferedRequestLimiter(16, 8, 9));
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBufferedRequestLimiter(16, 2, 3));

        Assert.Equal(4, new ApiJsonBufferedRequestLimiter(64).MaxConcurrentBufferedRequestsPerActor);
        Assert.Equal(2, new ApiJsonBufferedRequestLimiter(8, 2).MaxConcurrentBufferedRequestsPerActor);
        Assert.Equal(1, new ApiJsonBufferedRequestLimiter(1).MaxConcurrentBufferedRequestsPerActor);
        Assert.Equal(3, new ApiJsonBufferedRequestLimiter(16, 8, 3).MaxConcurrentBufferedRequestsPerActor);

        var limiter = new ApiJsonBufferedRequestLimiter(16, 8, 3);
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(" ", "tenant:a"));
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire("actor:a", " "));
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire(new string('x', 513), "tenant:a"));
        Assert.Throws<ArgumentException>(() =>
            limiter.TryAcquire("actor:a", new string('x', 257)));

        for (var index = 0; index < 1000; index++)
        {
            using (var permit = limiter.TryAcquire(
                $"account:tenant-a:actor-{index}", "tenant:a"))
            {
                Assert.NotNull(permit);
                Assert.Equal(1, limiter.ActiveActorPartitions);
                Assert.Equal(1, limiter.ActiveTenantPartitions);
            }
            Assert.Equal(0, limiter.ActiveActorPartitions);
            Assert.Equal(0, limiter.ActiveTenantPartitions);
        }
    }

    [Fact]
    public async Task ActorTenantAndGlobalBufferedAdmissionAreAtomicUnderContention()
    {
        var limiter = new ApiJsonBufferedRequestLimiter(8, 6, 2);
        var held = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(_ => Task.Run(() =>
                limiter.TryAcquire("account:tenant-a:actor-one", "tenant:a"))));
        try
        {
            Assert.Equal(2, held.Count(x => x is not null));
            Assert.Equal(1, limiter.ActiveActorPartitions);
            Assert.Equal(1, limiter.ActiveTenantPartitions);
            Assert.Null(limiter.TryAcquire("account:tenant-a:actor-one", "tenant:a"));

            var other = new List<IDisposable>();
            try
            {
                foreach (var actor in new[] { "actor-two", "actor-three" })
                {
                    for (var i = 0; i < 2; i++)
                        other.Add(Assert.IsAssignableFrom<IDisposable>(
                            limiter.TryAcquire($"account:tenant-a:{actor}", "tenant:a")));
                }
                Assert.Equal(3, limiter.ActiveActorPartitions);
                Assert.Null(limiter.TryAcquire("account:tenant-a:actor-four", "tenant:a"));

                for (var i = 0; i < 2; i++)
                    other.Add(Assert.IsAssignableFrom<IDisposable>(
                        limiter.TryAcquire("account:tenant-b:actor-one", "tenant:b")));

                Assert.Equal(4, limiter.ActiveActorPartitions);
                Assert.Equal(2, limiter.ActiveTenantPartitions);
                Assert.Null(limiter.TryAcquire("account:tenant-c:actor-one", "tenant:c"));
            }
            finally
            {
                foreach (var permit in other)
                    permit.Dispose();
            }
        }
        finally
        {
            foreach (var permit in held.Where(x => x is not null))
            {
                permit!.Dispose();
                permit.Dispose();
            }
        }
        Assert.Equal(0, limiter.ActiveActorPartitions);
        Assert.Equal(0, limiter.ActiveTenantPartitions);
        using var reopened = limiter.TryAcquire("account:tenant-a:actor-one", "tenant:a");
        Assert.NotNull(reopened);
    }

    private static async Task AssertAcceptedAsync(
        ApiJsonBufferedRequestLimiter bufferLimiter,
        ApiJsonBodyReadConcurrencyLimiter receiveLimiter,
        string tenant, string actor)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var ctx = Request(bytes, tenant, actor);
        var reached = false;
        await ApiJsonBodyBudget.InvokeAsync(ctx, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, 1024, 5000, receiveLimiter, bufferLimiter);
        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Same(bytes, ctx.Request.Body);
    }

    private static DefaultHttpContext Request(Stream bytes, string tenant, string actor)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/privacy/erase";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = bytes;
        ctx.Response.Body = new MemoryStream();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim("sub", actor)],
            "synthetic-test"));
        return ctx;
    }

    private static async Task<string> ErrorTextAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body,
            Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
