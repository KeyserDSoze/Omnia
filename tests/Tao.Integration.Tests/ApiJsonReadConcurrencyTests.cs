using System.Text;
using Microsoft.AspNetCore.Http;
using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// Synthetic concurrency contract. No real user data or network.
/// Shared limiter must bound in-flight JSON body reads, not DB handlers.
/// </summary>
public sealed class ApiJsonReadConcurrencyTests
{
    [Fact]
    public void ConcurrentReadBudgetHasExplicitSafeBounds()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBodyReadConcurrencyLimiter(0));
        Assert.Throws<InvalidOperationException>(() =>
            new ApiJsonBodyReadConcurrencyLimiter(257));
        Assert.Equal(1, new ApiJsonBodyReadConcurrencyLimiter(1).MaxConcurrentReads);
        Assert.Equal(32, new ApiJsonBodyReadConcurrencyLimiter(32).MaxConcurrentReads);
        Assert.Equal(256, new ApiJsonBodyReadConcurrencyLimiter(256).MaxConcurrentReads);
    }

    [Fact]
    public async Task SaturatedReaderReturns503WithoutReadingOrExecutingHandler()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        using var heldStream = new ControlledBodyStream();
        var heldContext = Request(heldStream);
        var heldHandlerCalled = false;
        var holding = ApiJsonBodyBudget.InvokeAsync(heldContext, _ =>
        {
            heldHandlerCalled = true;
            return Task.CompletedTask;
        }, 1024, 5000, limiter);
        await heldStream.WaitForReadAsync();

        using var rejectedStream = new FailOnReadStream();
        var rejected = Request(rejectedStream);
        var rejectedHandlerCalled = false;
        await ApiJsonBodyBudget.InvokeAsync(rejected, _ =>
        {
            rejectedHandlerCalled = true;
            return Task.CompletedTask;
        }, 1024, 5000, limiter);

        Assert.False(rejectedHandlerCalled);
        Assert.Equal(0, rejectedStream.ReadCalls);
        Assert.Same(rejectedStream, rejected.Request.Body);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, rejected.Response.StatusCode);
        Assert.Equal("no-store", rejected.Response.Headers.CacheControl.ToString());
        Assert.Equal("1", rejected.Response.Headers["Retry-After"].ToString());
        var response = await ResponseTextAsync(rejected);
        Assert.Contains("request_body_busy", response);
        Assert.DoesNotContain("synthetic-owner", response);

        heldStream.Finish();
        await holding;
        Assert.True(heldHandlerCalled);
        Assert.Same(heldStream, heldContext.Request.Body);
        await AssertSmallJsonSucceedsAsync(limiter);
    }

    [Fact]
    public async Task RejectsInvalidMediaAndKnownOversizeBeforeAdmissionEvenWhenSaturated()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        using var heldStream = new ControlledBodyStream();
        var holding = ApiJsonBodyBudget.InvokeAsync(Request(heldStream),
            _ => Task.CompletedTask, 1024, 5000, limiter);
        await heldStream.WaitForReadAsync();

        using var invalidStream = new FailOnReadStream();
        var invalid = Request(invalidStream);
        invalid.Request.ContentType = "text/plain";
        await ApiJsonBodyBudget.InvokeAsync(invalid,
            _ => throw new Exception("handler must not run"), 1024, 5000, limiter);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, invalid.Response.StatusCode);
        Assert.Equal(0, invalidStream.ReadCalls);

        using var oversizeStream = new FailOnReadStream();
        var oversized = Request(oversizeStream);
        oversized.Request.ContentLength = 2048;
        await ApiJsonBodyBudget.InvokeAsync(oversized,
            _ => throw new Exception("handler must not run"), 1024, 5000, limiter);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversized.Response.StatusCode);
        Assert.Equal(0, oversizeStream.ReadCalls);

        heldStream.Finish();
        await holding;
    }

    [Fact]
    public async Task TimedOutOrDisconnectedReaderAlwaysReleasesPermit()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        using (var timeoutStream = new ControlledBodyStream())
        {
            var timedOut = Request(timeoutStream);
            await ApiJsonBodyBudget.InvokeAsync(timedOut,
                _ => throw new Exception("handler must not run"), 1024, 110, limiter);
            Assert.Equal(StatusCodes.Status408RequestTimeout, timedOut.Response.StatusCode);
        }
        await AssertSmallJsonSucceedsAsync(limiter);

        using var abortedStream = new ControlledBodyStream();
        using var abortedCts = new CancellationTokenSource();
        var aborted = Request(abortedStream);
        aborted.RequestAborted = abortedCts.Token;
        var inFlight = ApiJsonBodyBudget.InvokeAsync(aborted,
            _ => throw new Exception("handler must not run"), 1024, 5000, limiter);
        await abortedStream.WaitForReadAsync();
        abortedCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inFlight);
        Assert.NotEqual(StatusCodes.Status408RequestTimeout, aborted.Response.StatusCode);
        await AssertSmallJsonSucceedsAsync(limiter);
    }

    [Fact]
    public async Task ChunkedOversizeOrStreamExceptionAlsoReturnReaderPermit()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        // The body has no trusted Content-Length; the middleware discovers
        // oversize only after acquiring a read permit.
        using (var oversizedStream = new MemoryStream(new byte[2048]))
        {
            var oversized = Request(oversizedStream);
            oversized.Request.ContentLength = null;
            await ApiJsonBodyBudget.InvokeAsync(oversized,
                _ => throw new Exception("oversized body must not reach handler"),
                1024, 5000, limiter);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversized.Response.StatusCode);
        }
        await AssertSmallJsonSucceedsAsync(limiter);

        using (var brokenStream = new FailOnReadStream())
        {
            var broken = Request(brokenStream);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ApiJsonBodyBudget.InvokeAsync(broken,
                    _ => throw new Exception("broken body must not reach handler"),
                    1024, 5000, limiter));
        }
        await AssertSmallJsonSucceedsAsync(limiter);
    }

    [Fact]
    public async Task CompletedReadReleasesPermitBeforeSlowDownstreamHandler()
    {
        var limiter = new ApiJsonBodyReadConcurrencyLimiter(1);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var context = Request(source);
        var handlerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ApiJsonBodyBudget.InvokeAsync(context, async _ =>
        {
            handlerEntered.TrySetResult(true);
            await releaseHandler.Task;
        }, 1024, 5000, limiter);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // If the permit is accidentally retained for DB/AI handler execution,
        // this second read would be rejected 503.
        await AssertSmallJsonSucceedsAsync(limiter);
        releaseHandler.TrySetResult(true);
        await first;
        Assert.Same(source, context.Request.Body);
    }

    private static async Task AssertSmallJsonSucceedsAsync(ApiJsonBodyReadConcurrencyLimiter limiter)
    {
        const string json = "{\"confirmation\":\"NO\"}";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var context = Request(source);
        string? received = null;
        await ApiJsonBodyBudget.InvokeAsync(context, async http =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8,
                leaveOpen: true);
            received = await reader.ReadToEndAsync();
        }, 1024, 5000, limiter);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(json, received);
        Assert.Same(source, context.Request.Body);
    }

    private static DefaultHttpContext Request(Stream input)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = "application/json";
        context.Request.Body = input;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ResponseTextAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class ControlledBodyStream : Stream
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForReadAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        public void Finish() => _done.TrySetResult(true);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadCoreAsync(cancellationToken));
        private async Task<int> ReadCoreAsync(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            await _done.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FailOnReadStream : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            throw new InvalidOperationException("The saturated request must not be read.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("The saturated request must not be read.");
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
