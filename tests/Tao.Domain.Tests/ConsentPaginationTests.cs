using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ConsentPaginationTests
{
    [Fact]
    public void CanonicalConsentCursorRoundTripsAndDoesNotAcceptMemoryCursor()
    {
        var point = new ConsentPageCursor(new DateTimeOffset(
            2026, 11, 23, 9, 10, 11, TimeSpan.Zero).AddTicks(1230),
            Guid.Parse("8a87f405-104e-4d98-ac13-56d7587c4b83"));
        var encoded = point.Encode();
        Assert.Equal(73, encoded.Length);
        Assert.StartsWith("c1.", encoded);
        Assert.True(ConsentPageCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(point, decoded);
        Assert.False(MemoryPageCursor.TryDecode(encoded, out _));
        Assert.False(ConsentPageCursor.TryDecode(
            new MemoryPageCursor(point.GrantedAt, point.Id).Encode(), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("c1.")]
    [InlineData("c2.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void NonCanonicalCursorFailsClosed(string token) =>
        Assert.False(ConsentPageCursor.TryDecode(token, out _));

    [Fact]
    public async Task ConsentPageServiceRejectsInvalidSizeAndCursorBeforeRepository()
    {
        var repo = new CountingRepository();
        var service = new ConsentPageService(repo);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PageAsync("tenant", "actor", 0, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PageAsync("tenant", "actor", 51, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PageAsync("tenant", "actor", 25, "not-a-cursor", default));
        Assert.Equal(0, repo.Reads);
        await service.PageAsync("tenant", "actor", null, null, default);
        Assert.Equal(1, repo.Reads);
        Assert.Equal(25, repo.LastLimit);
    }

    private sealed class CountingRepository : IConsentPageRepository
    {
        public int Reads { get; private set; }
        public int LastLimit { get; private set; }

        public Task<ConsentPage> PageAsync(string tenantId, string ownerSubject,
            int limit, ConsentPageCursor? before, CancellationToken ct)
        {
            Reads++;
            LastLimit = limit;
            return Task.FromResult(new ConsentPage([], null));
        }
    }
}
