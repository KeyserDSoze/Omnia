using System.Text;
using Microsoft.AspNetCore.Http;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Synthetic cancellation-aware body transports; no private user content,
/// no real network or PostgreSQL. The main integration suite separately
/// verifies authentication, 413/415, 429 and real RLS/DB behavior.
/// </summary>
public sealed class ApiJsonBodyReadTimeoutTests
{
    [Fact]
    public void ReadTimeoutIsExplicitlyBoundedAndNeverAccidentallyDisabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(0));
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(-1));
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(99));
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(30001));
        Assert.Equal(100, ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(100));
        Assert.Equal(10000, ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(10000));
        Assert.Equal(30000, ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(30000));
    }

    [Fact]
    public async Task BlockingChunkedBodyTimesOut408BeforeHandlerWithoutEchoingBytes()
    {
        using var source = new SlowlyDeliveredStream(Timeout.InfiniteTimeSpan);
        var context = Request(source);
        var handled = false;
        await ApiJsonBodyBudget.InvokeAsync(context,
            _ => { handled = true; return Task.CompletedTask; },
            1024, 120);

        Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(handled);
        Assert.Equal(1, source.ReadCalls);
        var body = await ResponseText(context);
        Assert.Contains("request_body_timeout", body);
        Assert.DoesNotContain("synthetic-owner", body);
    }

    [Fact]
    public async Task TrickleOfSingleBytesCannotResetAbsoluteReadDeadline()
    {
        using var source = new SlowlyDeliveredStream(TimeSpan.FromMilliseconds(30));
        var context = Request(source);
        var handled = false;
        await ApiJsonBodyBudget.InvokeAsync(context,
            _ => { handled = true; return Task.CompletedTask; },
            1024, 180);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        Assert.InRange(source.ReadCalls, 2, 15);
        Assert.Contains("request_body_timeout", await ResponseText(context));
    }

    [Fact]
    public async Task ActualClientDisconnectMustNotBeMisrepresentedAsDeadline408()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new SlowlyDeliveredStream(Timeout.InfiniteTimeSpan);
        var context = Request(stream);
        context.RequestAborted = cancelled.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApiJsonBodyBudget.InvokeAsync(context,
                _ => throw new Exception("binder must not execute"),
                1024, 120));
        Assert.NotEqual(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task ShortJsonBodyStillReachesHandlerAndIsRestoredExactly()
    {
        var payload = Encoding.UTF8.GetBytes("{\"confirmation\":\"NO\"}");
        using var input = new MemoryStream(payload);
        var context = Request(input);
        var received = "";
        await ApiJsonBodyBudget.InvokeAsync(context, async http =>
        {
            using var reader = new StreamReader(http.Request.Body,
                Encoding.UTF8, leaveOpen: true);
            received = await reader.ReadToEndAsync();
        }, 1024, 120);
        Assert.Equal(Encoding.UTF8.GetString(payload), received);
        Assert.Same(input, context.Request.Body);
        Assert.NotEqual(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
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

    private static async Task<string> ResponseText(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body,
            Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class SlowlyDeliveredStream(TimeSpan delay) : Stream
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
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(DelayedReadAsync(buffer, cancellationToken));

        private async Task<int> DelayedReadAsync(
            Memory<byte> buffer, CancellationToken ct)
        {
            ReadCalls++;
            await Task.Delay(delay, ct);
            buffer.Span[0] = (byte)'x';
            return 1;
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
