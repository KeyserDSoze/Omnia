using Npgsql;
using Tao.Domain;
using Tao.Infrastructure;
using Xunit;

namespace Tao.Integration.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task PostgreSqlPersistedMemoryIsIdempotentAndScoped()
    {
        var connection = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        Assert.False(string.IsNullOrWhiteSpace(connection),
            "TAO_TEST_DB must point to a disposable pgvector PostgreSQL test database.");
        var runtimeConnection = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(runtimeConnection),
            "TAO_TEST_APP_DB must be a restricted PostgreSQL login, never owner/superuser.");
        await using (var admin = NpgsqlDataSource.Create(connection!))
            await new PostgresMigrator(admin, "tao_app").MigrateAsync();
        await using var db = NpgsqlDataSource.Create(runtimeConnection!);
        await PostgresRuntimeGuard.VerifyAsync(db);
        var repo = new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create());
        var now = DateTimeOffset.UtcNow;
        var tenant = "tenant-" + Guid.NewGuid().ToString("N");
        await new PostgresConsentRepository(db).GrantAsync(ConsentGrant.Issue(
            tenant, "owner-a", "manual", ConsentGrant.MemoryCapture,
            "privacy-v1", now, now.AddDays(1)), CancellationToken.None);
        var external = Guid.NewGuid().ToString("N");
        var entry = MemoryEntry.Capture(tenant, "owner-a", "manual", external,
            "text/plain", "Meeting with client", "{\"text\":\"Meeting\"}",
            "Meeting with client", ["lavoro"], DateTimeOffset.UtcNow);
        var stored = await repo.UpsertAsync(entry, CancellationToken.None);
        var again = await repo.UpsertAsync(entry, CancellationToken.None);
        Assert.Equal(stored.Id, again.Id);
        var own = await repo.RecentAsync(tenant, "owner-a", 100, CancellationToken.None);
        Assert.Contains(own, x => x.Id == stored.Id);
        var otherUser = await repo.RecentAsync(tenant, "owner-b", 100, CancellationToken.None);
        Assert.DoesNotContain(otherUser, x => x.Id == stored.Id);
        var otherTenant = await repo.RecentAsync("tenant-b", "owner-a", 100, CancellationToken.None);
        Assert.DoesNotContain(otherTenant, x => x.Id == stored.Id);
    }
}
