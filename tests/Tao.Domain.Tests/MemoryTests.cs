using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public class MemoryTests
{
    [Fact]
    public void CaptureKeepsTenantAndSourceProvenance()
    {
        var item = MemoryEntry.Capture("tenant-a", "user-a", "outlook", "message-1",
            "text/plain", "Call with client", "{\"text\":\"Call\"}",
            "Call with client", ["lavoro"], DateTimeOffset.UtcNow);
        Assert.Equal("tenant-a", item.TenantId);
        Assert.Equal("user-a", item.OwnerSubject);
        Assert.Equal("message-1", item.ExternalId);
        Assert.Contains("lavoro", item.Tags);
        Assert.Null(item.Embedding);
    }

    [Theory]
    [InlineData("", "user")]
    [InlineData("tenant", "")]
    public void CaptureRejectsMissingScope(string tenant, string owner) =>
        Assert.Throws<ArgumentException>(() => MemoryEntry.Capture(tenant, owner,
            "manual", "e1", "text/plain", "hello", "{}", "hello", [],
            DateTimeOffset.UtcNow));

    [Fact]
    public void EmbeddingRejectsWrongDimensions() =>
        Assert.Throws<ArgumentException>(() => MemoryEntry.Capture("t", "u", "manual",
            "1", "text/plain", "hello", "{}", "hello", [],
            DateTimeOffset.UtcNow, new float[4], "test-model"));

    [Theory]
    [InlineData("Riunione con il cliente", "lavoro")]
    [InlineData("Cena con un amico", "relazioni")]
    [InlineData("Una passeggiata", "personale")]
    public void RulesProvideTraceableInitialTags(string input, string expected) =>
        Assert.Contains(expected, MemoryCatalog.Classify(input));
}
