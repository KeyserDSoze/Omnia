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
        var runtimeConnection = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(runtimeConnection),
            "OMNIA_TEST_APP_DB must be a restricted PostgreSQL login.");
        await using (var admin = NpgsqlDataSource.Create(connection!))
            await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        var db = NpgsqlDataSource.Create(runtimeConnection!);
        await PostgresRuntimeGuard.VerifyAsync(db);
        return db;
    }

    [Fact]
    public async Task ConsentIsRequiredForEveryWriteAndRevocationStopsFutureWrites()
    {
        await using var db = await OpenAsync();
        var consents = new PostgresConsentRepository(db);
        var memory = new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create());
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
            new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create()).UpsertAsync(MemoryEntry.Capture(
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
        var memory = new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create());
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
        await using (var scope = await PostgresActorScope.OpenAsync(
            db, tenant, "u", CancellationToken.None))
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE consent_grants SET granted_at = now() - interval '2 days', " +
                "expires_at = now() - interval '1 day' WHERE id = @id",
                scope.Connection, scope.Transaction);
            cmd.Parameters.AddWithValue("id", second.Id);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            await scope.CommitAsync(CancellationToken.None);
        }
        await Assert.ThrowsAsync<ConsentRequiredException>(() => memory.UpsertAsync(
            MemoryEntry.Capture(tenant, "u", source, "expired", "text/plain",
                "Synthetic", "{}", "Synthetic", [], now), CancellationToken.None));
    }


    [Fact]
    public async Task RevokingHistoricalGrantIdWithdrawsCurrentSameScopeAfterAnotherDeviceRegrants()
    {
        // Restricted PostgreSQL runtime role, real FORCE RLS and actor locks.
        // The original grant is deliberately no longer active when revoked.
        await using var db = await OpenAsync();
        var repo = new PostgresConsentRepository(db);
        var memory = new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create());
        var tenant = "stale-revoke-" + Guid.NewGuid().ToString("N");
        var source = "manual-" + Guid.NewGuid().ToString("N");
        var unrelatedSource = "calendar-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        Task<ConsentGrant> Grant(string t, string subject, string s) =>
            repo.GrantAsync(ConsentGrant.Issue(t, subject, s,
                ConsentGrant.MemoryCapture, "privacy-v1", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7)), default);

        var stale = await Grant(tenant, "alice", source);
        var current = await Grant(tenant, "alice", source);
        var otherSource = await Grant(tenant, "alice", unrelatedSource);
        var otherOwner = await Grant(tenant, "bob", source);
        var otherTenant = await Grant(tenant + "-else", "alice", source);

        var before = await repo.ListAsync(tenant, "alice", default);
        var superseded = Assert.Single(before, g => g.Id == stale.Id);
        var originalRevokedAt = Assert.IsType<DateTimeOffset>(superseded.RevokedAt);
        Assert.Contains(before, g => g.Id == current.Id && g.IsActiveAt(DateTimeOffset.UtcNow));
        Assert.False(superseded.IsActiveAt(DateTimeOffset.UtcNow));

        // UI/device A still holds stale.Id, while device B already issued current.
        // RED: old implementation returns true but only updates stale, leaving
        // current active and allowing new ingestion despite apparent withdrawal.
        Assert.True(await repo.RevokeAsync(tenant, "alice", stale.Id,
            DateTimeOffset.UtcNow, default));
        var after = await repo.ListAsync(tenant, "alice", default);
        Assert.Equal(originalRevokedAt,
            Assert.Single(after, g => g.Id == stale.Id).RevokedAt);
        Assert.NotNull(Assert.Single(after, g => g.Id == current.Id).RevokedAt);
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            repo.EnsureActiveAsync(tenant, "alice", source,
                ConsentGrant.MemoryCapture, default));

        var denied = MemoryEntry.Capture(tenant, "alice", source,
            "post-withdrawal-" + Guid.NewGuid().ToString("N"), "text/plain",
            "Synthetic after withdrawal", "{}", "Synthetic after withdrawal",
            [], now);
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            memory.UpsertAsync(denied, default));

        // Revoking an old handle must never affect a different purpose/source
        // or tenant/owner, even if they share the same source text.
        await repo.EnsureActiveAsync(tenant, "alice", unrelatedSource,
            ConsentGrant.MemoryCapture, default);
        await repo.EnsureActiveAsync(tenant, "bob", source,
            ConsentGrant.MemoryCapture, default);
        await repo.EnsureActiveAsync(tenant + "-else", "alice", source,
            ConsentGrant.MemoryCapture, default);
        Assert.Contains(after, g => g.Id == otherSource.Id && g.RevokedAt is null);
        Assert.False(await repo.RevokeAsync(tenant, "bob", stale.Id,
            DateTimeOffset.UtcNow, default));
        Assert.False(await repo.RevokeAsync(tenant + "-else", "alice", stale.Id,
            DateTimeOffset.UtcNow, default));
        Assert.False(await repo.RevokeAsync(tenant, "alice", Guid.NewGuid(),
            DateTimeOffset.UtcNow, default));

        // Repeating a valid withdrawal is idempotent; a NEW deliberate
        // acceptance can grant the scope again, never silently after revoke.
        Assert.True(await repo.RevokeAsync(tenant, "alice", stale.Id,
            DateTimeOffset.UtcNow, default));
        var renewed = await Grant(tenant, "alice", source);
        await repo.EnsureActiveAsync(tenant, "alice", source,
            ConsentGrant.MemoryCapture, default);
        Assert.Null(Assert.Single(await repo.ListAsync(tenant, "alice", default),
            g => g.Id == renewed.Id).RevokedAt);
        Assert.NotEqual(current.Id, renewed.Id);
        Assert.NotEqual(otherOwner.Id, renewed.Id);
        Assert.NotEqual(otherTenant.Id, renewed.Id);
    }


    [Fact]
    public async Task ActiveManualGrantSurvivesBoundedLedgerAfterManyNewerRevokedOrExpiredGrants()
    {
        // #178 RED: the old chronological LIMIT 200 hides an older effective
        // manual grant, although the actual PostgreSQL consent gate allows it.
        // All fixtures run inside the restricted RLS actor transaction.
        await using var db = await OpenAsync();
        var repo = new PostgresConsentRepository(db);
        var tenant = "consent-list-" + Guid.NewGuid().ToString("N");
        const string owner = "list-owner";
        var now = DateTimeOffset.UtcNow;

        var manual = await repo.GrantAsync(ConsentGrant.Issue(
            tenant, owner, "manual", ConsentGrant.MemoryCapture,
            "privacy-v1", now.AddDays(-3), now.AddDays(3)), default);
        var calendar = await repo.GrantAsync(ConsentGrant.Issue(
            tenant, owner, "calendar", ConsentGrant.MemoryCapture,
            "privacy-v1", now.AddDays(-2), now.AddDays(3)), default);

        // 260 newer historical records, mixed expired-not-revoked and
        // revoked-not-expired, must not obscure either older active scope.
        await using (var scope = await PostgresActorScope.OpenAsync(db, tenant, owner, default))
        {
            await using var fixture = new NpgsqlCommand("""
                INSERT INTO consent_grants
                    (id, tenant_id, owner_sub, source, purpose, policy_version,
                     granted_at, expires_at, revoked_at)
                SELECT gen_random_uuid(), @tenant, @owner, 'archive-' || n::text,
                       'memory.capture', 'privacy-v1',
                       now() - interval '1 minute',
                       CASE WHEN n % 2 = 0 THEN now() - interval '30 seconds'
                            ELSE now() + interval '1 day' END,
                       CASE WHEN n % 2 = 1 THEN now() - interval '30 seconds'
                            ELSE NULL END
                FROM generate_series(1, 260) AS n
                """, scope.Connection, scope.Transaction);
            fixture.Parameters.AddWithValue("tenant", tenant);
            fixture.Parameters.AddWithValue("owner", owner);
            Assert.Equal(260, await fixture.ExecuteNonQueryAsync());
            await scope.CommitAsync(default);
        }

        // The real server still considers both scopes authorized.
        await repo.EnsureActiveAsync(tenant, owner, "manual",
            ConsentGrant.MemoryCapture, default);
        await repo.EnsureActiveAsync(tenant, owner, "calendar",
            ConsentGrant.MemoryCapture, default);

        var listed = await repo.ListAsync(tenant, owner, default);
        Assert.Equal(200, listed.Count); // bounded response is preserved
        Assert.Equal(manual.Id, listed[0].Id); // manual UI priority
        Assert.Contains(listed, g => g.Id == calendar.Id && g.IsActiveAt(now));
        Assert.DoesNotContain(listed, g => g.Source.StartsWith("archive-") &&
            g.IsActiveAt(DateTimeOffset.UtcNow));

        // No cross-owner or cross-tenant leakage despite oversized history.
        Assert.Empty(await repo.ListAsync(tenant, "another-owner", default));
        Assert.Empty(await repo.ListAsync(tenant + "-else", owner, default));
        Assert.False(await repo.RevokeAsync(tenant, "another-owner",
            manual.Id, DateTimeOffset.UtcNow, default));

        // A grant exposed to the Web user remains individually revocable.
        Assert.True(await repo.RevokeAsync(tenant, owner, manual.Id,
            DateTimeOffset.UtcNow, default));
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            repo.EnsureActiveAsync(tenant, owner, "manual",
                ConsentGrant.MemoryCapture, default));
        await repo.EnsureActiveAsync(tenant, owner, "calendar",
            ConsentGrant.MemoryCapture, default);
    }

}
