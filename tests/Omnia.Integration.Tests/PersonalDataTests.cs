using System.Text.Json;
using Npgsql;
using Omnia.Application;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Privacy contract tests run against the real non-owner PostgreSQL role with FORCE RLS.
/// All values are synthetic; never print personal export JSON to CI logs.
/// </summary>
public sealed class PersonalDataTests
{
    [Fact]
    public async Task ExportIsCompleteTenantScopedAndErasureIsAtomicAndIdempotent()
    {
        var adminConnection = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appConnection = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminConnection));
        Assert.False(string.IsNullOrWhiteSpace(appConnection));
        await using (var admin = NpgsqlDataSource.Create(adminConnection!))
            await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var runtime = NpgsqlDataSource.Create(appConnection!);
        await PostgresRuntimeGuard.VerifyAsync(runtime);

        var data = new PostgresPersonalDataService(runtime, TestEnvelopeCrypto.Create());
        var consents = new PostgresConsentRepository(runtime);
        var memories = new PostgresMemoryRepository(runtime, TestEnvelopeCrypto.Create());
        var tenant = "privacy-" + Guid.NewGuid().ToString("N");
        var otherTenant = tenant + "-other";
        var now = DateTimeOffset.UtcNow;

        async Task SaveAsync(string t, string subject, string item, bool vector = false)
        {
            await consents.GrantAsync(ConsentGrant.Issue(t, subject, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);
            var memory = MemoryEntry.Capture(t, subject, "manual", item,
                "text/plain", "raw-" + item, "{\"value\":\"normalized-" + item + "\"}",
                "summary-" + item, ["lavoro"], now,
                vector ? Enumerable.Repeat(0.25f, 768).ToArray() : null,
                vector ? "nomic-synthetic" : null);
            await memories.UpsertAsync(memory, default);
        }

        await SaveAsync(tenant, "alice", "one", vector: true);
        await SaveAsync(tenant, "bob", "other-user");
        await SaveAsync(otherTenant, "alice", "other-tenant");

        // Consent history is part of export; a re-grant preserves a revoked predecessor.
        await consents.GrantAsync(ConsentGrant.Issue(tenant, "alice", "manual",
            ConsentGrant.MemoryCapture, "privacy-v2", now, now.AddDays(10)), default);

        async Task<JsonDocument> ExportAsync(string t, string sub)
        {
            await using var output = new MemoryStream();
            await data.WriteExportAsync(t, sub, output, default);
            Assert.True(output.Length > 40);
            return JsonDocument.Parse(output.ToArray());
        }

        using (var first = await ExportAsync(tenant, "alice"))
        {
            var root = first.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(tenant, root.GetProperty("tenantId").GetString());
            Assert.Equal("alice", root.GetProperty("subject").GetString());
            var entries = root.GetProperty("memoryEntries");
            Assert.Equal(1, entries.GetArrayLength());
            var item = entries[0];
            Assert.Equal("raw-one", item.GetProperty("rawContent").GetString());
            Assert.Equal("normalized-one", item.GetProperty("normalized").GetProperty("value").GetString());
            Assert.Equal("summary-one", item.GetProperty("summary").GetString());
            Assert.Equal("one", item.GetProperty("externalId").GetString());
            Assert.Equal(768, item.GetProperty("embedding").GetArrayLength());
            Assert.Equal("nomic-synthetic", item.GetProperty("embeddingModel").GetString());
            Assert.Equal(2, root.GetProperty("consentGrants").GetArrayLength());
        }

        // Kestrel forbids synchronous I/O. The export contract must never call
        // Stream.Write or Stream.Flush synchronously, even during writer disposal.
        await using (var asyncOnly = new AsyncWriteOnlyStream())
        {
            await data.WriteExportAsync(tenant, "alice", asyncOnly, default);
            using var snapshot = JsonDocument.Parse(asyncOnly.Snapshot());
            Assert.Single(snapshot.RootElement.GetProperty("memoryEntries").EnumerateArray());
        }

        using (var bob = await ExportAsync(tenant, "bob"))
        {
            Assert.Single(bob.RootElement.GetProperty("memoryEntries").EnumerateArray());
            Assert.Equal("raw-other-user",
                bob.RootElement.GetProperty("memoryEntries")[0].GetProperty("rawContent").GetString());
        }
        using (var distant = await ExportAsync(otherTenant, "alice"))
            Assert.Equal("raw-other-tenant",
                distant.RootElement.GetProperty("memoryEntries")[0].GetProperty("rawContent").GetString());

        var erased = await data.EraseAsync(tenant, "alice", default);
        Assert.Equal(1, erased.DeletedMemories);
        Assert.Equal(2, erased.DeletedConsents);
        Assert.True(erased.CompletedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(0, (await data.EraseAsync(tenant, "alice", default)).DeletedMemories);

        Assert.Empty(await memories.RecentAsync(tenant, "alice", 20, default));
        Assert.Empty(await consents.ListAsync(tenant, "alice", default));
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            memories.UpsertAsync(MemoryEntry.Capture(tenant, "alice", "manual",
                "retry", "text/plain", "should-not-save", "{}", "should-not-save", [], now), default));

        using (var erasedExport = await ExportAsync(tenant, "alice"))
        {
            Assert.Empty(erasedExport.RootElement.GetProperty("memoryEntries").EnumerateArray());
            Assert.Empty(erasedExport.RootElement.GetProperty("consentGrants").EnumerateArray());
        }
        Assert.Single(await memories.RecentAsync(tenant, "bob", 20, default));
        Assert.Single(await memories.RecentAsync(otherTenant, "alice", 20, default));

        // Unscoped SQL sees nothing (no leaked actor GUC in a pooled connection).
        await using var unscoped = runtime.CreateCommand("SELECT count(*) FROM memory_entries");
        Assert.Equal(0L, (long)(await unscoped.ExecuteScalarAsync() ?? -1L));
    }

    private sealed class AsyncWriteOnlyStream : Stream
    {
        private readonly MemoryStream _data = new();
        public byte[] Snapshot() => _data.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new InvalidOperationException("Sync flush is forbidden.");
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Sync write is forbidden.");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException("Sync write is forbidden.");
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken ct) => _data.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default) => _data.WriteAsync(buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => _data.DisposeAsync();
    }

    [Fact]
    public async Task EmptySubjectAndTenantCannotExportOrErase()
    {
        var appConnection = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(appConnection));
        await using var db = NpgsqlDataSource.Create(appConnection!);
        var service = new PostgresPersonalDataService(db, TestEnvelopeCrypto.Create());
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => service.WriteExportAsync("tenant", "", output, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EraseAsync("", "subject", default));
    }
}
