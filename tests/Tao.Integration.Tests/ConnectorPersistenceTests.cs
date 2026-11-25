using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// Connected accounts and the daily triage state on real PostgreSQL with the restricted
/// FORCE-RLS API role. All tokens and identities are synthetic.
/// </summary>
public sealed class ConnectorPersistenceTests
{
    [Fact]
    public async Task AuthorizationStateIsSealedSingleUseAndActorBound()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var store = new PostgresConnectorStore(app, TestEnvelopeCrypto.Create());
        var tenant = "conn-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        const string verifier = "synthetic-pkce-verifier-value";

        await store.SaveAuthorizationAsync(tenant, "alice", ConnectorProviders.Microsoft, hash, verifier,
            DateTimeOffset.UtcNow.AddMinutes(10), default);

        await using (var raw = admin.CreateCommand(
            "SELECT sealed_verifier FROM connector_authorizations WHERE state_hash = @hash"))
        {
            raw.Parameters.AddWithValue("hash", hash);
            var envelope = (byte[])(await raw.ExecuteScalarAsync())!;
            Assert.DoesNotContain(verifier, Encoding.UTF8.GetString(envelope));
        }
        Assert.Null(await store.ConsumeAuthorizationAsync(tenant, "mallory", hash, default));
        Assert.Null(await store.ConsumeAuthorizationAsync(tenant + "-x", "alice", hash, default));
        var pending = await store.ConsumeAuthorizationAsync(tenant, "alice", hash, default);
        Assert.Equal((ConnectorProviders.Microsoft, verifier), (pending!.Provider, pending.CodeVerifier));
        Assert.Null(await store.ConsumeAuthorizationAsync(tenant, "alice", hash, default));

        // Abandoned flows are bounded per actor.
        for (var i = 0; i < PostgresConnectorStore.MaxPendingAuthorizations + 5; i++)
            await store.SaveAuthorizationAsync(tenant, "alice", ConnectorProviders.Google,
                SHA256.HashData(BitConverter.GetBytes(i).Concat(hash).ToArray()), verifier,
                DateTimeOffset.UtcNow.AddMinutes(10), default);
        await using var count = admin.CreateCommand(
            "SELECT count(*) FROM connector_authorizations WHERE tenant_id = @tenant");
        count.Parameters.AddWithValue("tenant", tenant);
        Assert.Equal((long)PostgresConnectorStore.MaxPendingAuthorizations, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CredentialsAreSealedRowBoundAndInvisibleToOtherActors()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var store = new PostgresConnectorStore(app, TestEnvelopeCrypto.Create());
        var tenant = "conn-" + Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        ConnectorCredentials Credentials(string external, string access) => new(
            new OAuthTokens(access, "refresh-" + access, expires, ["Mail.Read"]),
            new ConnectedIdentity(external, "Alice " + external, external + "@example.test"));

        var first = await store.UpsertAccountAsync(tenant, "alice", ConnectorProviders.Microsoft,
            Credentials("graph-1", "access-token-one"), default);
        var again = await store.UpsertAccountAsync(tenant, "alice", ConnectorProviders.Microsoft,
            Credentials("graph-1", "access-token-two"), default);
        var second = await store.UpsertAccountAsync(tenant, "alice", ConnectorProviders.Google,
            Credentials("google-1", "access-token-three"), default);
        var bobs = await store.UpsertAccountAsync(tenant, "bob", ConnectorProviders.Microsoft,
            Credentials("graph-1", "access-token-bob"), default);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal([first.Id, second.Id], (await store.ListAccountsAsync(tenant, "alice", default)).Select(a => a.Id));
        Assert.Equal([bobs.Id], (await store.ListAccountsAsync(tenant, "bob", default)).Select(a => a.Id));

        await using (var raw = admin.CreateCommand(
            "SELECT string_agg(encode(sealed_credentials, 'escape'), '') FROM connector_accounts WHERE tenant_id = @tenant"))
        {
            raw.Parameters.AddWithValue("tenant", tenant);
            var all = (string)(await raw.ExecuteScalarAsync())!;
            Assert.DoesNotContain("access-token", all);
            Assert.DoesNotContain("refresh-", all);
            Assert.DoesNotContain("@example.test", all);
        }

        // Bob cannot use, refresh or delete Alice's account even with its id.
        Assert.Null(await store.UseCredentialsAsync(tenant, "bob", first.Id,
            (c, _) => Task.FromResult(c), default));
        Assert.Null(await store.DeleteAccountAsync(tenant, "bob", first.Id, default));

        var refreshed = await store.UseCredentialsAsync(tenant, "alice", first.Id, (c, _) =>
            Task.FromResult(c with { Tokens = c.Tokens with { AccessToken = "access-token-rotated" } }), default);
        Assert.Equal("access-token-rotated", refreshed!.Tokens.AccessToken);
        var reread = await store.UseCredentialsAsync(tenant, "alice", first.Id, (c, _) => Task.FromResult(c), default);
        Assert.Equal(("access-token-rotated", "refresh-access-token-two"),
            (reread!.Tokens.AccessToken, reread.Tokens.RefreshToken));

        // A ciphertext moved to another row (or actor) does not open: AAD binds the account id.
        await using (var swap = admin.CreateCommand("""
            UPDATE connector_accounts SET sealed_credentials =
              (SELECT sealed_credentials FROM connector_accounts WHERE id = @from)
            WHERE id = @to
            """))
        {
            swap.Parameters.AddWithValue("from", first.Id);
            swap.Parameters.AddWithValue("to", second.Id);
            Assert.Equal(1, await swap.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            store.UseCredentialsAsync(tenant, "alice", second.Id, (c, _) => Task.FromResult(c), default));

        var removed = await store.DeleteAccountAsync(tenant, "alice", first.Id, default);
        Assert.Equal(("access-token-rotated", ConnectorProviders.Microsoft),
            (removed!.Credentials.Tokens.AccessToken, removed.Provider));
        Assert.Single(await store.ListAccountsAsync(tenant, "bob", default));
    }

    [Fact]
    public async Task TriageDecisionsAreScopedExpireAndFollowTheAccountLifecycle()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var connectors = new PostgresConnectorStore(app, TestEnvelopeCrypto.Create());
        var triage = new PostgresTriageStore(app);
        var tenant = "triage-" + Guid.NewGuid().ToString("N");
        var account = await connectors.UpsertAccountAsync(tenant, "alice", ConnectorProviders.Microsoft,
            new ConnectorCredentials(new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), []),
                new ConnectedIdentity("graph-x", "Alice", null)), default);
        var key = WorkItemKey.Format(ConnectorProviders.Microsoft, account.Id, WorkItemKind.Task, "L|T");
        var other = WorkItemKey.Format(ConnectorProviders.Google, Guid.NewGuid(), WorkItemKind.Mail, "m");
        var now = DateTimeOffset.UtcNow;

        await triage.SaveAsync(tenant, "alice", key, new TriageRecord(TriageDecision.Done, now, null, true), default);
        await triage.SaveAsync(tenant, "alice", key, new TriageRecord(TriageDecision.Later, now, now.AddHours(2), false), default);
        await triage.SaveAsync(tenant, "alice", other, new TriageRecord(TriageDecision.Dismissed, now, null, false), default);
        await triage.AuditAsync(tenant, "alice", account.Id, "task.complete", "succeeded", default);

        var mine = await triage.GetAsync(tenant, "alice", [key, other], default);
        Assert.Equal(TriageDecision.Later, mine[key].Decision);
        Assert.True(mine[key].AppliedToSource); // a provider write is never "forgotten" by a later swipe
        Assert.Empty(await triage.GetAsync(tenant, "bob", [key, other], default));

        // Undo: only the actor's own decision, never one that already completed the task at the provider.
        var third = WorkItemKey.Format(ConnectorProviders.Google, Guid.NewGuid(), WorkItemKind.Mail, "undo");
        await triage.SaveAsync(tenant, "alice", third, new TriageRecord(TriageDecision.Dismissed, now, null, false), default);
        Assert.Equal(TriageUndoOutcome.NotFound, await triage.RemoveAsync(tenant, "bob", third, default));
        Assert.Equal(TriageUndoOutcome.AppliedToSource, await triage.RemoveAsync(tenant, "alice", key, default));
        Assert.Equal(TriageUndoOutcome.Removed, await triage.RemoveAsync(tenant, "alice", third, default));
        Assert.Equal(TriageUndoOutcome.NotFound, await triage.RemoveAsync(tenant, "alice", third, default));
        Assert.False((await triage.GetAsync(tenant, "alice", [third], default)).ContainsKey(third));

        await using (var age = admin.CreateCommand(
            "UPDATE triage_decisions SET expires_at = now() - interval '1 second' WHERE item_key = @key"))
        {
            age.Parameters.AddWithValue("key", other);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        Assert.Equal([key], (await triage.GetAsync(tenant, "alice", [key, other], default)).Keys);

        await connectors.DeleteAccountAsync(tenant, "alice", account.Id, default);
        Assert.Empty(await triage.GetAsync(tenant, "alice", [key], default));

        // Unscoped SQL through the API role sees nothing at all.
        foreach (var table in PostgresRuntimeGuard.PersonalTables)
        {
            await using var unscoped = app.CreateCommand($"SELECT count(*) FROM {table}");
            Assert.Equal(0L, (long)(await unscoped.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task ExportNeverContainsTokensAndErasureRemovesEveryConnectorRow()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var crypto = TestEnvelopeCrypto.Create();
        var connectors = new PostgresConnectorStore(app, crypto);
        var triage = new PostgresTriageStore(app);
        var data = new PostgresPersonalDataService(app, crypto);
        var tenant = "conn-export-" + Guid.NewGuid().ToString("N");
        var account = await connectors.UpsertAccountAsync(tenant, "alice", ConnectorProviders.Google,
            new ConnectorCredentials(new OAuthTokens("secret-access", "secret-refresh",
                DateTimeOffset.UtcNow.AddHours(1), ["openid", "email"]),
                new ConnectedIdentity("google-sub-1", "Alice Rossi", "alice@example.test")), default);
        var key = WorkItemKey.Format(ConnectorProviders.Google, account.Id, WorkItemKind.Mail, "m1");
        await triage.SaveAsync(tenant, "alice", key,
            new TriageRecord(TriageDecision.NotDone, DateTimeOffset.UtcNow, null, false), default);
        await triage.AuditAsync(tenant, "alice", account.Id, "task.complete", "failed", default);
        await connectors.SaveAuthorizationAsync(tenant, "alice", ConnectorProviders.Microsoft,
            SHA256.HashData(Guid.NewGuid().ToByteArray()), "verifier", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await connectors.UpsertAccountAsync(tenant, "bob", ConnectorProviders.Google,
            new ConnectorCredentials(new OAuthTokens("bob-access", null, DateTimeOffset.UtcNow.AddHours(1), []),
                new ConnectedIdentity("google-sub-2", "Bob", null)), default);

        await using (var output = new MemoryStream())
        {
            await data.WriteExportAsync(tenant, "alice", output, default);
            var text = Encoding.UTF8.GetString(output.ToArray());
            Assert.DoesNotContain("secret-access", text);
            Assert.DoesNotContain("secret-refresh", text);
            Assert.DoesNotContain("verifier", text);
            Assert.DoesNotContain("bob", text, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var exported = Assert.Single(root.GetProperty("connectorAccounts").EnumerateArray());
            Assert.Equal(("Alice Rossi", "alice@example.test", "google-sub-1", "sealed-not-exported"),
                (exported.GetProperty("displayName").GetString(), exported.GetProperty("email").GetString(),
                 exported.GetProperty("externalAccountId").GetString(), exported.GetProperty("tokens").GetString()));
            Assert.Equal(key, Assert.Single(root.GetProperty("triageDecisions").EnumerateArray())
                .GetProperty("itemKey").GetString());
            Assert.Equal("failed", Assert.Single(root.GetProperty("connectorActions").EnumerateArray())
                .GetProperty("outcome").GetString());
            Assert.Single(root.GetProperty("pendingConnectorAuthorizations").EnumerateArray());
        }

        var erased = await data.EraseAsync(tenant, "alice", default);
        Assert.Equal((1, 3), (erased.DeletedConnectorAccounts, erased.DeletedWorkState));
        Assert.Empty(await connectors.ListAccountsAsync(tenant, "alice", default));
        Assert.Single(await connectors.ListAccountsAsync(tenant, "bob", default));
        await using var left = admin.CreateCommand("""
            SELECT (SELECT count(*) FROM connector_authorizations WHERE tenant_id = @t AND owner_sub = 'alice')
                 + (SELECT count(*) FROM triage_decisions WHERE tenant_id = @t AND owner_sub = 'alice')
                 + (SELECT count(*) FROM connector_actions WHERE tenant_id = @t AND owner_sub = 'alice')
            """);
        left.Parameters.AddWithValue("t", tenant);
        Assert.Equal(0L, (long)(await left.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RetentionWorkerPurgesExpiredWorkStateWithoutTableAccess()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var retentionCs = Environment.GetEnvironmentVariable("TAO_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(retentionCs));
        await new PostgresMigrator(admin, "tao_app", "tao_retention").MigrateAsync();
        await using var worker = NpgsqlDataSource.Create(retentionCs!);
        await PostgresRetentionRepository.VerifyWorkerRoleAsync(worker);
        var repository = new PostgresRetentionRepository(worker);
        while (await repository.PurgeWorkStateBatchAsync(500, default) > 0) { }

        var tenant = "conn-ret-" + Guid.NewGuid().ToString("N");
        var triage = new PostgresTriageStore(app);
        var keys = Enumerable.Range(0, 3).Select(i =>
            WorkItemKey.Format(ConnectorProviders.Microsoft, Guid.NewGuid(), WorkItemKind.Mail, "m" + i)).ToArray();
        foreach (var key in keys)
            await triage.SaveAsync(tenant, "alice", key,
                new TriageRecord(TriageDecision.Done, DateTimeOffset.UtcNow, null, false), default);
        await using (var age = admin.CreateCommand(
            "UPDATE triage_decisions SET expires_at = now() - interval '1 second' WHERE item_key = ANY(@keys)"))
        {
            age.Parameters.AddWithValue("keys", keys.Take(2).ToArray());
            Assert.Equal(2, await age.ExecuteNonQueryAsync());
        }

        Assert.Equal(2, await repository.PurgeWorkStateBatchAsync(500, default));
        Assert.Equal(0, await repository.PurgeWorkStateBatchAsync(500, default));
        Assert.Equal([keys[2]], (await triage.GetAsync(tenant, "alice", keys, default)).Keys);
        await using (var direct = worker.CreateCommand("SELECT count(*) FROM connector_accounts"))
            await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
        await using (var forbidden = app.CreateCommand("SELECT public.tao_purge_expired_work_state(10)"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                () => forbidden.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task AssistantUsageIsAtomicScopedExportedErasedAndExpires()
    {
        await using var db = await DatabasesAsync();
        var (admin, app) = (db.Admin, db.App);
        var retentionCs = Environment.GetEnvironmentVariable("TAO_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(retentionCs));
        await new PostgresMigrator(admin, "tao_app", "tao_retention").MigrateAsync();
        await using var worker = NpgsqlDataSource.Create(retentionCs!);
        var repository = new PostgresRetentionRepository(worker);
        while (await repository.PurgeWorkStateBatchAsync(500, default) > 0) { }

        var usage = new PostgresAssistantUsageStore(app);
        var data = new PostgresPersonalDataService(app, TestEnvelopeCrypto.Create());
        var tenant = "usage-" + Guid.NewGuid().ToString("N");
        var day = new DateOnly(2026, 11, 25);

        // The call budget is enforced by one atomic statement: concurrent requests cannot overshoot it.
        var reserved = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            usage.TryReserveCallAsync(tenant, "alice", day, 3, default)));
        Assert.Equal(3, reserved.Count(r => r));
        await usage.AddTokensAsync(tenant, "alice", day, 1200, default);
        await usage.AddTokensAsync(tenant, "alice", day, 300, default);
        Assert.Equal(new AssistantUsage(3, 1500), await usage.GetAsync(tenant, "alice", day, default));
        Assert.Equal(new AssistantUsage(0, 0), await usage.GetAsync(tenant, "alice", day.AddDays(1), default));
        Assert.Equal(new AssistantUsage(0, 0), await usage.GetAsync(tenant, "bob", day, default));
        Assert.True(await usage.TryReserveCallAsync(tenant, "bob", day, 3, default));

        await using (var output = new MemoryStream())
        {
            await data.WriteExportAsync(tenant, "alice", output, default);
            using var json = JsonDocument.Parse(output.ToArray());
            var row = Assert.Single(json.RootElement.GetProperty("assistantUsage").EnumerateArray());
            Assert.Equal(("2026-11-25", 3, 1500L), (row.GetProperty("day").GetString(),
                row.GetProperty("calls").GetInt32(), row.GetProperty("tokens").GetInt64()));
        }

        await using (var age = admin.CreateCommand(
            "UPDATE assistant_usage SET expires_at = now() - interval '1 second' WHERE tenant_id = @t AND owner_sub = 'bob'"))
        {
            age.Parameters.AddWithValue("t", tenant);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        Assert.Equal(1, await repository.PurgeWorkStateBatchAsync(500, default));
        Assert.Equal(new AssistantUsage(3, 1500), await usage.GetAsync(tenant, "alice", day, default));

        Assert.Equal(1, (await data.EraseAsync(tenant, "alice", default)).DeletedWorkState);
        Assert.Equal(new AssistantUsage(0, 0), await usage.GetAsync(tenant, "alice", day, default));
    }

    private sealed record Databases(NpgsqlDataSource Admin, NpgsqlDataSource App) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Admin.DisposeAsync();
        }
    }

    private static async Task<Databases> DatabasesAsync()
    {
        var adminCs = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "tao_app").MigrateAsync();
        var app = NpgsqlDataSource.Create(appCs!);
        await PostgresRuntimeGuard.VerifyAsync(app);
        return new Databases(admin, app);
    }
}
