using Npgsql;
using Omnia.Application;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

public sealed class ConsentPersistenceTests
{
    private static async Task<NpgsqlDataSource> OpenAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        Assert.False(string.IsNullOrWhiteSpace(connection), "Disposable OMNIA_TEST_DB required.");
        var db = NpgsqlDataSource.Create(connection!);
        await new PostgresMigrator(db).MigrateAsync();
        return db;
    }

    [Fact]
    public async Task ConsentIsRequiredForEveryWriteAndRevocationStopsFutureWrites()
    {
        await using var db = await OpenAsync();
        var consents = new PostgresConsentRepository(db);
        var memory = new PostgresMemoryRepository(db);
        var source = "manual-" + Guid.NewGuid().ToString("N");
        var tenant = "tenant-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        MemoryEntry Item(string external) => MemoryEntry.Capture(tenant, "owner-a", source, external,
            "text/plain", "Synthetic event", "{\"schemaVersion\":1}",
            "Synthetic event", ["personale"], now);

        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            memory.UpsertAsync(Item("before"), CancellationToken.None));

        var consent = await consents.GrantAsync(ConsentGrant.Issue(tenant, "owner-a",
            source, ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)),
            CancellationToken.None);

        var saved = await memory.UpsertAsync(Item("allowed"), CancellationToken.None);
        Assert.NotEqual(Guid.Empty, saved.Id);

        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            new PostgresMemoryRepository(db).UpsertAsync(MemoryEntry.Capture(
                tenant, "owner-b", source, "other", "text/plain", "hello", "{}",
                "hello", [], now), CancellationToken.None));

        Assert.True(await consents.RevokeAsync(tenant, "owner-a", consent.Id,
            DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await consents.RevokeAsync(tenant, "owner-b", consent.Id,
            DateTimeOffset.UtcNow, CancellationToken.None));
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            memory.UpsertAsync(Item("after"), CancellationToken.None));

        // Revoking stops subsequent ingestion but does not silently erase historical data;
        // deletion/export are separate privacy release gates (#31).
        var previous = await memory.RecentAsync(tenant, "owner-a", 25, CancellationToken.None);
        Assert.Contains(previous, x => x.Id == saved.Id);
    }

    [Fact]
    public async Task RegrantSupersedesOldScopeAndExpiredGrantCannotAuthorizeWrites()
    {
        await using var db = await OpenAsync();
        var repo = new PostgresConsentRepository(db);
        var memory = new PostgresMemoryRepository(db);
        var tenant = "t-" + Guid.NewGuid().ToString("N");
        var source = "crm-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var first = await repo.GrantAsync(ConsentGrant.Issue(tenant, "u", source,
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddMinutes(20)), CancellationToken.None);
        var second = await repo.GrantAsync(ConsentGrant.Issue(tenant, "u", source,
            ConsentGrant.MemoryCapture, "privacy-v2", now, now.AddDays(1)), CancellationToken.None);
        var grants = await repo.ListAsync(tenant, "u", CancellationToken.None);
        Assert.Contains(grants, x => x.Id == first.Id && x.RevokedAt is not null);
        Assert.Contains(grants, x => x.Id == second.Id && x.RevokedAt is null);

        // Expire directly to simulate the passage of time without long sleeps.
        await using (var cmd = db.CreateCommand(
            "UPDATE consent_grants SET granted_at = now() - interval '2 days', " +
            "expires_at = now() - interval '1 day' WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", second.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<ConsentRequiredException>(() => memory.UpsertAsync(
            MemoryEntry.Capture(tenant, "u", source, "expired", "text/plain",
                "Synthetic", "{}", "Synthetic", [], now), CancellationToken.None));
    }
}
