using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GrantIsNormalizedAndBoundToSourcePurposeAndOwner()
    {
        var grant = ConsentGrant.Issue("tenant-a", "owner-a", "CRM", ConsentGrant.MemoryCapture,
            "privacy-v1", Now, Now.AddDays(30));
        Assert.Equal("crm", grant.Source);
        Assert.Equal("memory.capture", grant.Purpose);
        Assert.Equal("tenant-a", grant.TenantId);
        Assert.Equal("owner-a", grant.OwnerSubject);
        Assert.True(grant.IsActiveAt(Now.AddMinutes(1)));
        Assert.False(grant.IsActiveAt(Now.AddDays(30)));
        Assert.False(grant.Revoke(Now.AddDays(1)).IsActiveAt(Now.AddDays(2)));
    }

    [Theory]
    [InlineData("mail.read")]
    [InlineData("message.send")]
    [InlineData("speech.recording")]
    [InlineData("")]
    public void UnsupportedPurposesFailClosed(string purpose) =>
        Assert.Throws<ArgumentException>(() => ConsentGrant.Issue("t", "u", "manual",
            purpose, "privacy-v1", Now, Now.AddDays(1)));

    [Fact]
    public void BrowserSpeechIsItsOwnPurpose()
    {
        var grant = ConsentGrant.Issue("t", "u", "voice", ConsentGrant.BrowserSpeech, "speech-v1",
            Now, Now.AddDays(30));
        Assert.Equal("speech.browser", grant.Purpose);
        Assert.Equal("voice", grant.Source);
    }

    [Fact]
    public void GrantCannotExceedNinetyDays() =>
        Assert.Throws<ArgumentException>(() => ConsentGrant.Issue("t", "u", "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", Now, Now.AddDays(91)));

    [Theory]
    [InlineData("has spaces")]
    [InlineData("../secrets")]
    [InlineData("")]
    public void InvalidSourceIsRejected(string source) =>
        Assert.Throws<ArgumentException>(() => ConsentGrant.Issue("t", "u", source,
            ConsentGrant.MemoryCapture, "privacy-v1", Now, Now.AddDays(1)));

    [Fact]
    public async Task ExplicitAcknowledgementIsMandatory()
    {
        var service = new ConsentService(new NeverCalledConsentRepository(), TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GrantAsync("t", "u",
            new GrantConsentCommand("manual", ConsentGrant.MemoryCapture, "privacy-v1",
                DateTimeOffset.UtcNow.AddDays(1), false), CancellationToken.None));
    }

    [Fact]
    public async Task NoConsentMeansEmbeddingNeverReceivesTheData()
    {
        var repo = new FakeMemoryRepository();
        var embedding = new CountingEmbeddingProvider();
        var service = new MemoryCatalog(repo, embedding, new DenyingConsentGuard());
        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.CaptureAsync("t", "u",
            new CaptureMemoryCommand("manual", "id-1", "text/plain", "Sensitive content", null),
            CancellationToken.None));
        Assert.Equal(0, embedding.Calls);
        Assert.Equal(0, repo.Writes);
    }

    private sealed class DenyingConsentGuard : IConsentGuard
    {
        public Task EnsureActiveAsync(string tenant, string subject, string source,
            string purpose, CancellationToken ct) =>
            throw new ConsentRequiredException();
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<EmbeddingVector?> EmbedAsync(string content, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<EmbeddingVector?>(null);
        }
    }

    private sealed class FakeMemoryRepository : IMemoryRepository
    {
        public int Writes { get; private set; }
        public Task<MemoryEntry> UpsertAsync(MemoryEntry entry, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult(entry);
        }
        public Task<IReadOnlyList<MemorySummary>> RecentAsync(string tenant, string user,
            int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemorySummary>>([]);
    }

    private sealed class NeverCalledConsentRepository : IConsentRepository
    {
        public Task<ConsentGrant> GrantAsync(ConsentGrant grant, CancellationToken ct) =>
            throw new InvalidOperationException("Must never persist unacknowledged consent.");
        public Task<bool> RevokeAsync(string tenant, string subject, Guid id,
            DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentGrant>> ListAsync(string tenant, string subject,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
