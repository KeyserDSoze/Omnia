using Omnia.Application;
using Omnia.Domain;
using Xunit;

namespace Omnia.Domain.Tests;

public class CatalogTests
{
    [Fact]
    public async Task CapturePreservesRawAndCreatesNormalizedSummary()
    {
        var repo = new FakeRepository();
        var catalog = new MemoryCatalog(repo, new NoVector());
        var saved = await catalog.CaptureAsync("t1", "u1",
            new CaptureMemoryCommand("crm", "ev-9", "application/json",
                "{\"message\":\"Riunione cliente\"}", null), CancellationToken.None);
        Assert.Equal("crm", saved.Source);
        Assert.Contains("Riunione", saved.Summary);
        Assert.Contains("schemaVersion", saved.NormalizedJson);
        Assert.Equal("{\"message\":\"Riunione cliente\"}", saved.RawContent);
        Assert.Null(saved.Embedding);
    }

    [Fact]
    public async Task CaptureRejectsBrokenJson()
    {
        var catalog = new MemoryCatalog(new FakeRepository(), new NoVector());
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            catalog.CaptureAsync("t", "u",
                new CaptureMemoryCommand("crm", "1", "application/json", "{bad", null),
                CancellationToken.None));
    }

    private sealed class NoVector : IEmbeddingProvider
    {
        public Task<EmbeddingVector?> EmbedAsync(string text, CancellationToken ct) =>
            Task.FromResult<EmbeddingVector?>(null);
    }
    private sealed class FakeRepository : IMemoryRepository
    {
        public Task<MemoryEntry> UpsertAsync(MemoryEntry item, CancellationToken ct) =>
            Task.FromResult(item);
        public Task<IReadOnlyList<MemorySummary>> RecentAsync(
            string tenant, string subject, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemorySummary>>([]);
    }
}
