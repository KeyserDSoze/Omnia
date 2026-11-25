using System.Security.Cryptography;
using System.Text;
using System.Web;
using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ConnectorServiceTests
{
    private static readonly Uri Redirect = new("https://tao.example.test/connections/callback");

    [Fact]
    public async Task AuthorizationUsesPkceS256AndStoresOnlyTheStateHash()
    {
        var (service, store, provider, _) = Create();
        var uri = await service.StartAsync("t", "alice", ConnectorProviders.Microsoft, default);

        var query = HttpUtility.ParseQueryString(uri.Query);
        var state = query["state"]!;
        var pending = Assert.Single(store.Pending);
        Assert.Equal(ConnectorService.HashState(state), pending.Key.Hash);
        Assert.DoesNotContain(state, Encoding.UTF8.GetString(pending.Key.Hash));
        Assert.Equal(Challenge(pending.Value.CodeVerifier), query["code_challenge"]);
        Assert.Equal(Redirect, provider.LastRedirect);
    }

    [Fact]
    public async Task CompletionIsSingleUseAndBoundToTheActorThatStartedIt()
    {
        var (service, store, provider, _) = Create();
        var state = State(await service.StartAsync("t", "alice", ConnectorProviders.Microsoft, default));

        await Assert.ThrowsAsync<ConnectorAuthorizationException>(() =>
            service.CompleteAsync("t", "mallory", state, "code-1", default));
        var account = await service.CompleteAsync("t", "alice", state, "code-1", default);

        Assert.Equal(ConnectorProviders.Microsoft, account.Provider);
        Assert.Equal("Alice", account.DisplayName);
        Assert.Equal(("code-1", Redirect), (provider.LastCode, provider.LastRedirect));
        Assert.Equal(provider.LastVerifierChallenge, Challenge(provider.LastVerifier!));
        await Assert.ThrowsAsync<ConnectorAuthorizationException>(() =>
            service.CompleteAsync("t", "alice", state, "code-1", default));
        Assert.Single(store.Accounts);
    }

    [Fact]
    public async Task ExpiredStateIsRejected()
    {
        var (service, _, _, clock) = Create();
        var state = State(await service.StartAsync("t", "alice", ConnectorProviders.Microsoft, default));
        clock.Advance(ConnectorService.AuthorizationLifetime + TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<ConnectorAuthorizationException>(() =>
            service.CompleteAsync("t", "alice", state, "code", default));
        Assert.Equal("expired_state", error.Message);
    }

    [Fact]
    public async Task UnconfiguredProviderOrMissingPublicUrlCannotStart()
    {
        var (service, _, _, _) = Create(configured: false);
        await Assert.ThrowsAsync<ConnectorNotConfiguredException>(() =>
            service.StartAsync("t", "alice", ConnectorProviders.Microsoft, default));
        var noUrl = new ConnectorService([new FakeProvider()], new InMemoryConnectorStore(),
            TimeProvider.System, new ConnectorSettings(null));
        Assert.False(Assert.Single(noUrl.Providers()).Configured);
        await Assert.ThrowsAsync<ConnectorNotConfiguredException>(() =>
            noUrl.StartAsync("t", "alice", ConnectorProviders.Microsoft, default));
    }

    [Fact]
    public async Task ExpiringTokenIsRefreshedAndKeepsANonRotatedRefreshToken()
    {
        var (service, store, provider, clock) = Create();
        var account = await Connect(service);
        clock.Advance(TimeSpan.FromMinutes(59));
        provider.RotateRefreshToken = false;

        var (_, credentials) = await service.AccessAsync("t", "alice", account, default);
        Assert.Equal("access-2", credentials.Tokens.AccessToken);
        Assert.Equal("refresh-1", credentials.Tokens.RefreshToken);
        Assert.Equal("access-2", store.Accounts.Single().Value.Credentials.Tokens.AccessToken);
    }

    [Fact]
    public async Task MissingRefreshTokenRequiresReconnect()
    {
        var (service, _, provider, clock) = Create();
        provider.IssueRefreshToken = false;
        var account = await Connect(service);
        clock.Advance(TimeSpan.FromHours(2));
        var error = await Assert.ThrowsAsync<ConnectorUnavailableException>(() =>
            service.AccessAsync("t", "alice", account, default));
        Assert.Equal("reconnect_required", error.Code);
    }

    [Fact]
    public async Task TodayOrdersByPriorityAttachesDecisionsAndReportsFailingAccounts()
    {
        var (service, store, provider, clock) = Create();
        var account = await Connect(service);
        var broken = await store.UpsertAccountAsync("t", "alice", ConnectorProviders.Microsoft,
            new ConnectorCredentials(new OAuthTokens("broken", null, clock.GetUtcNow().AddHours(1), []),
                new ConnectedIdentity("other", "Other", null)), default);
        var now = clock.GetUtcNow();
        provider.Items = id => id == broken.Id
            ? throw new HttpRequestException("down")
            : [
                new WorkItem(ConnectorProviders.Microsoft, id, WorkItemKind.Mail, "m1", "FYI"),
                new WorkItem(ConnectorProviders.Microsoft, id, WorkItemKind.Task, "l|t1", "Offerta",
                    DueAt: now.AddHours(-2))
            ];
        var triage = new InMemoryTriageStore();
        var today = new TodayService(service, triage, clock);
        var mailKey = WorkItemKey.Format(ConnectorProviders.Microsoft, account.Id, WorkItemKind.Mail, "m1");
        await today.DecideAsync("t", "alice", new TriageCommand(mailKey, "later", 30, false), default);

        var snapshot = await today.GetAsync("t", "alice", now.AddHours(-9), now.AddHours(15), default);

        Assert.Equal(["l|t1", "m1"], snapshot.Items.Select(x => x.Item.ExternalId));
        Assert.Equal(TriageDecision.Later, snapshot.Items[1].Triage!.Decision);
        Assert.Equal(now.AddMinutes(30), snapshot.Items[1].Triage!.SnoozedUntil);
        Assert.Equal("ok", snapshot.Accounts.Single(a => a.AccountId == account.Id).Status);
        Assert.Equal("unavailable", snapshot.Accounts.Single(a => a.AccountId == broken.Id).Status);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            today.GetAsync("t", "alice", now, now.AddHours(73), default));
    }

    [Fact]
    public async Task ApplyingDoneToSourceCompletesOnlyTasksAndIsAudited()
    {
        var (service, _, provider, clock) = Create();
        var account = await Connect(service);
        var triage = new InMemoryTriageStore();
        var today = new TodayService(service, triage, clock);
        var task = WorkItemKey.Format(ConnectorProviders.Microsoft, account.Id, WorkItemKind.Task, "l|t1");
        var mail = WorkItemKey.Format(ConnectorProviders.Microsoft, account.Id, WorkItemKind.Mail, "m1");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            today.DecideAsync("t", "alice", new TriageCommand(mail, "done", null, true), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            today.DecideAsync("t", "alice", new TriageCommand(task, "later", null, true), default));
        Assert.Empty(provider.Completed);

        var result = await today.DecideAsync("t", "alice", new TriageCommand(task, "done", null, true), default);
        Assert.True(result.AppliedToSource);
        Assert.Equal(["l|t1"], provider.Completed);
        Assert.Equal([(account.Id, "task.complete", "succeeded")], triage.Audit);

        provider.FailCompletion = true;
        await Assert.ThrowsAsync<ConnectorUnavailableException>(() =>
            today.DecideAsync("t", "alice", new TriageCommand(task, "done", null, true), default));
        Assert.Equal((account.Id, "task.complete", "failed"), triage.Audit[^1]);
    }

    [Fact]
    public async Task DecisionsOnAnotherActorsAccountAreRejected()
    {
        var (service, _, _, clock) = Create();
        var account = await Connect(service);
        var today = new TodayService(service, new InMemoryTriageStore(), clock);
        var key = WorkItemKey.Format(ConnectorProviders.Microsoft, account.Id, WorkItemKind.Mail, "m1");
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            today.DecideAsync("t", "mallory", new TriageCommand(key, "done", null, false), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            today.DecideAsync("t", "alice", new TriageCommand("not-a-key", "done", null, false), default));
    }

    private static async Task<ConnectorAccountSummary> Connect(ConnectorService service)
    {
        var state = State(await service.StartAsync("t", "alice", ConnectorProviders.Microsoft, default));
        return await service.CompleteAsync("t", "alice", state, "code-1", default);
    }

    private static (ConnectorService, InMemoryConnectorStore, FakeProvider, ManualClock) Create(
        bool configured = true)
    {
        var clock = new ManualClock();
        var store = new InMemoryConnectorStore();
        var provider = new FakeProvider { Configured = configured, Clock = clock };
        return (new ConnectorService([provider], store, clock, new ConnectorSettings(Redirect)),
            store, provider, clock);
    }

    private static string State(Uri uri) => HttpUtility.ParseQueryString(uri.Query)["state"]!;

    private static string Challenge(string verifier) => Convert.ToBase64String(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FakeProvider : IConnectorProvider
    {
        public bool Configured { get; set; } = true;
        public ManualClock Clock { get; set; } = new();
        public bool RotateRefreshToken { get; set; } = true;
        public bool IssueRefreshToken { get; set; } = true;
        public bool FailCompletion { get; set; }
        public Uri? LastRedirect { get; private set; }
        public string? LastCode { get; private set; }
        public string? LastVerifier { get; private set; }
        public string? LastVerifierChallenge { get; private set; }
        public List<string> Completed { get; } = [];
        public Func<Guid, IReadOnlyList<WorkItem>> Items { get; set; } = _ => [];
        private int _issued;

        public string Id => ConnectorProviders.Microsoft;
        public string DisplayName => "Microsoft 365";
        public bool IsConfigured => Configured;
        public IReadOnlyList<string> Capabilities => ["mail.read", "tasks.complete"];

        public Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri)
        {
            LastRedirect = redirectUri;
            LastVerifierChallenge = codeChallenge;
            return new Uri("https://login.example.test/authorize?state=" + Uri.EscapeDataString(state) +
                "&code_challenge=" + Uri.EscapeDataString(codeChallenge));
        }

        public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri,
            CancellationToken ct)
        {
            (LastCode, LastVerifier, LastRedirect) = (code, codeVerifier, redirectUri);
            return Task.FromResult(Issue());
        }

        public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var tokens = Issue();
            return Task.FromResult(RotateRefreshToken ? tokens : tokens with { RefreshToken = null });
        }

        private OAuthTokens Issue()
        {
            _issued++;
            return new OAuthTokens("access-" + _issued, IssueRefreshToken ? "refresh-" + _issued : null,
                Clock.GetUtcNow().AddHours(1), ["Mail.Read"]);
        }

        public Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult(new ConnectedIdentity("graph-user-1", "Alice", "alice@example.test"));

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
            string accessToken, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> capabilities,
            CancellationToken ct) =>
            Task.FromResult(Items(accountId));

        public Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct)
        {
            if (FailCompletion) throw new HttpRequestException("refused");
            Completed.Add(externalTaskId);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(OAuthTokens tokens, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InMemoryConnectorStore : IConnectorStore
    {
        public Dictionary<(string Tenant, string Owner, byte[] Hash), PendingAuthorization> Pending { get; } =
            new(new KeyComparer());
        public Dictionary<Guid, (string Tenant, string Owner, string Provider, ConnectorCredentials Credentials,
            DateTimeOffset ConnectedAt)> Accounts { get; } = [];

        public Task SaveAuthorizationAsync(string tenantId, string ownerSubject, string provider,
            byte[] stateHash, string codeVerifier, DateTimeOffset expiresAt, CancellationToken ct)
        {
            Pending[(tenantId, ownerSubject, stateHash)] = new PendingAuthorization(provider, codeVerifier, expiresAt);
            return Task.CompletedTask;
        }

        public Task<PendingAuthorization?> ConsumeAuthorizationAsync(string tenantId, string ownerSubject,
            byte[] stateHash, CancellationToken ct) =>
            Task.FromResult(Pending.Remove((tenantId, ownerSubject, stateHash), out var pending)
                ? pending : null);

        public Task<ConnectorAccountSummary> UpsertAccountAsync(string tenantId, string ownerSubject,
            string provider, ConnectorCredentials credentials, CancellationToken ct)
        {
            var existing = Accounts.FirstOrDefault(a => a.Value.Tenant == tenantId &&
                a.Value.Owner == ownerSubject && a.Value.Provider == provider &&
                a.Value.Credentials.Identity.ExternalId == credentials.Identity.ExternalId);
            var id = existing.Key == Guid.Empty ? Guid.NewGuid() : existing.Key;
            Accounts[id] = (tenantId, ownerSubject, provider, credentials, DateTimeOffset.UtcNow);
            return Task.FromResult(Summary(id));
        }

        public Task<IReadOnlyList<ConnectorAccountSummary>> ListAccountsAsync(string tenantId,
            string ownerSubject, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectorAccountSummary>>(Accounts
                .Where(a => a.Value.Tenant == tenantId && a.Value.Owner == ownerSubject)
                .Select(a => Summary(a.Key)).ToArray());

        public Task<RemovedConnectorAccount?> DeleteAccountAsync(string tenantId, string ownerSubject,
            Guid accountId, CancellationToken ct)
        {
            if (!Accounts.TryGetValue(accountId, out var row) || row.Tenant != tenantId || row.Owner != ownerSubject)
                return Task.FromResult<RemovedConnectorAccount?>(null);
            Accounts.Remove(accountId);
            return Task.FromResult<RemovedConnectorAccount?>(new(row.Provider, row.Credentials));
        }

        public async Task<ConnectorCredentials?> UseCredentialsAsync(string tenantId, string ownerSubject,
            Guid accountId, Func<ConnectorCredentials, CancellationToken, Task<ConnectorCredentials>> refresh,
            CancellationToken ct)
        {
            if (!Accounts.TryGetValue(accountId, out var row) || row.Tenant != tenantId || row.Owner != ownerSubject)
                return null;
            var updated = await refresh(row.Credentials, ct);
            Accounts[accountId] = row with { Credentials = updated };
            return updated;
        }

        private ConnectorAccountSummary Summary(Guid id)
        {
            var row = Accounts[id];
            return new ConnectorAccountSummary(id, row.Provider, row.Credentials.Identity.DisplayName,
                row.Credentials.Identity.Email, row.ConnectedAt, row.Credentials.Tokens.Scopes);
        }

        private sealed class KeyComparer : IEqualityComparer<(string, string, byte[])>
        {
            public bool Equals((string, string, byte[]) x, (string, string, byte[]) y) =>
                x.Item1 == y.Item1 && x.Item2 == y.Item2 && x.Item3.SequenceEqual(y.Item3);
            public int GetHashCode((string, string, byte[]) obj) =>
                HashCode.Combine(obj.Item1, obj.Item2, Convert.ToHexString(obj.Item3));
        }
    }

    private sealed class InMemoryTriageStore : ITriageStore
    {
        private readonly Dictionary<(string, string, string), TriageRecord> _records = [];
        public List<(Guid, string, string)> Audit { get; } = [];

        public Task<IReadOnlyDictionary<string, TriageRecord>> GetAsync(string tenantId, string ownerSubject,
            IReadOnlyCollection<string> keys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, TriageRecord>>(keys
                .Where(k => _records.ContainsKey((tenantId, ownerSubject, k)))
                .ToDictionary(k => k, k => _records[(tenantId, ownerSubject, k)]));

        public Task SaveAsync(string tenantId, string ownerSubject, string key, TriageRecord record,
            CancellationToken ct)
        {
            _records[(tenantId, ownerSubject, key)] = record;
            return Task.CompletedTask;
        }

        public Task AuditAsync(string tenantId, string ownerSubject, Guid accountId, string action,
            string outcome, CancellationToken ct)
        {
            Audit.Add((accountId, action, outcome));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TriageRecord>> RecentAsync(string tenantId, string ownerSubject,
            DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TriageRecord>>(_records
                .Where(r => r.Key.Item1 == tenantId && r.Key.Item2 == ownerSubject && r.Value.DecidedAt >= since)
                .Select(r => r.Value).ToArray());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
