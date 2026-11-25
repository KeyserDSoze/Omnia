using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// Synthetic middleware contract. The read-only admission permit MUST be
/// released before handler work, but a distinct bounded-buffer permit MUST
/// survive until the downstream handler returns. No private data or network.
/// </summary>
public sealed class ApiJsonBufferedRequestTests
{
    [Fact]
    public void BufferedRequestLimiterIsNonQueuingBoundedAndIdempotent()
    {
        Assert.Throws<InvalidOperationException>(() => new ApiJsonBufferedRequestLimiter(0));
        Assert.Throws<InvalidOperationException>(() => new ApiJsonBufferedRequestLimiter(257));
        var limiter = new ApiJsonBufferedRequestLimiter(1);
        Assert.Equal(1, limiter.MaxConcurrentBufferedRequests);
        using (var lease = limiter.TryAcquire("test-actor", "test-tenant"))
        {
            Assert.NotNull(lease);
            Assert.Null(limiter.TryAcquire("test-actor", "test-tenant"));
            lease.Dispose();
            lease.Dispose();
        }
        using var again = limiter.TryAcquire("test-actor", "test-tenant");
        Assert.NotNull(again);
        Assert.Equal(64,
            new ApiJsonBufferedRequestLimiter(64).MaxConcurrentBufferedRequests);
        Assert.Equal(256,
            new ApiJsonBufferedRequestLimiter(256).MaxConcurrentBufferedRequests);
    }

    [Fact]
    public async Task ParallelBufferAdmissionsCannotOvercommit()
    {
        var limiter = new ApiJsonBufferedRequestLimiter(3);
        var leases = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(_ => Task.Run(() => limiter.TryAcquire("test-actor", "test-tenant"))));
        try
        {
            Assert.Equal(3, leases.Count(x => x is not null));
            Assert.Null(limiter.TryAcquire("test-actor", "test-tenant"));
        }
        finally
        {
            foreach (var lease in leases.Where(x => x is not null))
            {
                lease!.Dispose();
                lease.Dispose();
            }
        }
        using var reopened = limiter.TryAcquire("test-actor", "test-tenant");
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task SlowHandlerRetainsBufferSlotButNotReadSlot()
    {
        var readLimiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        var bufferLimiter = new ApiJsonBufferedRequestLimiter(1);
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var firstContext = Request(firstStream);
        var handlerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ApiJsonBodyBudget.InvokeAsync(firstContext, async _ =>
        {
            handlerStarted.SetResult(true);
            await handlerRelease.Task;
        }, 1024, 5000, readLimiter, bufferLimiter);
        try
        {
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The old #159 contract must NOT regress: reading is finished,
            // so another caller could acquire the receiver permit.
            using (var freeReadLease = readLimiter.TryAcquire(
                ApiRateLimitPolicy.PartitionKey(firstContext),
                ApiRateLimitPolicy.TenantPartitionKey(firstContext)))
            {
                Assert.NotNull(freeReadLease);
            }

            using var secondStream = new FailOnReadStream();
            var secondContext = Request(secondStream);
            var handlerCalled = false;
            await ApiJsonBodyBudget.InvokeAsync(secondContext, _ =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            }, 1024, 5000, readLimiter, bufferLimiter);

            Assert.False(handlerCalled);
            Assert.Equal(0, secondStream.ReadCount);
            Assert.Same(secondStream, secondContext.Request.Body);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, secondContext.Response.StatusCode);
            Assert.Equal("1", secondContext.Response.Headers["Retry-After"].ToString());
            Assert.Equal("no-store", secondContext.Response.Headers.CacheControl.ToString());
            var error = await ReadResponseAsync(secondContext);
            Assert.Contains("request_buffer_busy", error);
            Assert.DoesNotContain("tenant", error);
            Assert.DoesNotContain("synthetic-owner", error);
        }
        finally
        {
            handlerRelease.TrySetResult(true);
            await first;
        }
        Assert.Same(firstStream, firstContext.Request.Body);
        await AssertAcceptedAsync(readLimiter, bufferLimiter);
    }

    [Fact]
    public async Task CheapInvalidMediaAndDeclaredOversizeWinEvenWhenBufferBudgetIsFull()
    {
        var bufferLimiter = new ApiJsonBufferedRequestLimiter(1);
        using var occupied = bufferLimiter.TryAcquire("test-actor", "test-tenant");
        Assert.NotNull(occupied);

        using var invalidBody = new FailOnReadStream();
        var invalid = Request(invalidBody);
        invalid.Request.ContentType = "text/plain";
        await ApiJsonBodyBudget.InvokeAsync(invalid,
            _ => throw new Exception("No handler for invalid media"),
            1024, 5000, null, bufferLimiter);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, invalid.Response.StatusCode);
        Assert.Equal(0, invalidBody.ReadCount);

        using var oversizedBody = new FailOnReadStream();
        var oversized = Request(oversizedBody);
        oversized.Request.ContentLength = 2048;
        await ApiJsonBodyBudget.InvokeAsync(oversized,
            _ => throw new Exception("No handler for declared oversize"),
            1024, 5000, null, bufferLimiter);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversized.Response.StatusCode);
        Assert.Equal(0, oversizedBody.ReadCount);

        var getContext = Request(Stream.Null);
        getContext.Request.Method = HttpMethods.Get;
        var getHandled = false;
        await ApiJsonBodyBudget.InvokeAsync(getContext, _ =>
        {
            getHandled = true;
            return Task.CompletedTask;
        }, 1024, 5000, null, bufferLimiter);
        Assert.True(getHandled);
        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
    }

    [Fact]
    public async Task FaultsOversizeAndTimeoutReleaseBufferForNextRequest()
    {
        var bufferLimiter = new ApiJsonBufferedRequestLimiter(1);

        using (var badStream = new FailOnReadStream())
        {
            var broken = Request(badStream);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ApiJsonBodyBudget.InvokeAsync(broken,
                    _ => throw new Exception("Handler should never run"),
                    1024, 5000, null, bufferLimiter));
        }
        await AssertAcceptedAsync(null, bufferLimiter);

        using (var oversized = new MemoryStream(new byte[2048]))
        {
            var context = Request(oversized);
            context.Request.ContentLength = null;
            await ApiJsonBodyBudget.InvokeAsync(context,
                _ => throw new Exception("Oversize should never reach handler"),
                1024, 5000, null, bufferLimiter);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        }
        await AssertAcceptedAsync(null, bufferLimiter);

        using (var blockedStream = new BlockUntilCanceledStream())
        {
            var timedOut = Request(blockedStream);
            await ApiJsonBodyBudget.InvokeAsync(timedOut,
                _ => throw new Exception("Timeout should never reach handler"),
                1024, 110, null, bufferLimiter);
            Assert.Equal(StatusCodes.Status408RequestTimeout, timedOut.Response.StatusCode);
        }
        await AssertAcceptedAsync(null, bufferLimiter);

        using (var abortStream = new BlockUntilCanceledStream())
        using (var abort = new CancellationTokenSource())
        {
            var disconnected = Request(abortStream);
            disconnected.RequestAborted = abort.Token;
            var operation = ApiJsonBodyBudget.InvokeAsync(disconnected,
                _ => throw new Exception("Disconnect should never reach handler"),
                1024, 5000, null, bufferLimiter);
            await abortStream.WaitForReadAsync();
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.NotEqual(StatusCodes.Status408RequestTimeout, disconnected.Response.StatusCode);
        }
        await AssertAcceptedAsync(null, bufferLimiter);

        using (var source = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
        {
            var fault = Request(source);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ApiJsonBodyBudget.InvokeAsync(fault,
                    _ => throw new InvalidOperationException("Synthetic handler failure"),
                    1024, 5000, null, bufferLimiter));
            Assert.Same(source, fault.Request.Body);
        }
        await AssertAcceptedAsync(null, bufferLimiter);
    }

    private static async Task AssertAcceptedAsync(
        ApiJsonBodyReadConcurrencyLimiter? readLimiter,
        ApiJsonBufferedRequestLimiter bufferLimiter)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var context = Request(body);
        var called = false;
        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, 1024, 5000, readLimiter, bufferLimiter);
        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Same(body, context.Request.Body);
    }

    private static DefaultHttpContext Request(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    private static async Task<string> ReadResponseAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body,
            Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FailOnReadStream : Stream
    {
        public int ReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("Synthetic stream must not be read");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Synthetic stream must not be read");
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
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockUntilCanceledStream : Stream
    {
        private readonly TaskCompletionSource<bool> _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForReadAsync() => _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
