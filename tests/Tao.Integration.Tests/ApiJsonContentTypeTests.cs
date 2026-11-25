using System.Text;
using Microsoft.AspNetCore.Http;
using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// No actual user payloads, credentials, DB connections or network.
/// A fail-on-read stream proves unsupported media types are rejected
/// before touching the body and before entering the application handler.
/// </summary>
public sealed class ApiJsonContentTypeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; charset=utf-16")]
    [InlineData("application/json, application/json")]
    public async Task UnsupportedMediaTypeIsRejected415BeforeFirstBodyRead(string? contentType)
    {
        using var source = new FailOnReadStream();
        var context = Request(source, contentType);
        var reachedHandler = false;

        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            reachedHandler = true;
            return Task.CompletedTask;
        }, 1024, 120);

        Assert.False(reachedHandler);
        Assert.Equal(0, source.ReadCalls);
        Assert.Same(source, context.Request.Body);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        var response = await ResponseText(context);
        Assert.Contains("unsupported_media_type", response);
        Assert.DoesNotContain("synthetic-owner", response);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/vnd.tao+json; charset=utf-8")]
    public async Task SupportedJsonMediaTypesPreserveBodyAndRestoreSource(string contentType)
    {
        const string json = "{\"confirmation\":\"NO\"}";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var context = Request(source, contentType);
        var received = string.Empty;
        await ApiJsonBodyBudget.InvokeAsync(context, async http =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
            received = await reader.ReadToEndAsync();
        }, 1024, 120);

        Assert.Equal(json, received);
        Assert.Same(source, context.Request.Body);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthPathIsNotRestrictedByJsonMediaTypeGuard()
    {
        using var stream = new FailOnReadStream();
        var context = Request(stream, "text/plain");
        context.Request.Path = "/health";
        var reachedHandler = false;
        await ApiJsonBodyBudget.InvokeAsync(context, _ =>
        {
            reachedHandler = true;
            return Task.CompletedTask;
        }, 1024, 120);
        Assert.True(reachedHandler);
        Assert.Equal(0, stream.ReadCalls);
    }

    private static DefaultHttpContext Request(Stream input, string? contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/privacy/erase";
        context.Request.ContentType = contentType;
        context.Request.Body = input;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ResponseText(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
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
            throw new InvalidOperationException("Unsupported media type must be rejected before reading.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("Unsupported media type must be rejected before reading.");
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
