using Tao.Application;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class SettingsAndDelegationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly WorkTools Tools = new(true, true, true, false, false, false, null);

    [Fact]
    public async Task TodayFetchesOnlyDelegatedCategories()
    {
        var h = new Harness();
        await h.Settings.SaveAsync("t", "alice", Settings(h.AccountId, ["calendar.read"]), default);

        var snapshot = await h.Today.GetAsync("t", "alice", Now.AddHours(-8), Now.AddHours(16), default);

        Assert.Equal(["calendar.read"], h.Provider.Requested.Single());
        Assert.Equal([WorkItemKind.Event], snapshot.Items.Select(i => i.Item.Kind));
    }

    [Fact]
    public async Task AccountWithoutReadDelegationsIsPausedAndNeverFetched()
    {
        var h = new Harness();
        await h.Settings.SaveAsync("t", "alice", Settings(h.AccountId, ["tasks.complete"]), default);

        var snapshot = await h.Today.GetAsync("t", "alice", Now.AddHours(-8), Now.AddHours(16), default);

        Assert.Empty(h.Provider.Requested);
        Assert.Equal("paused", Assert.Single(snapshot.Accounts).Status);
    }

    [Fact]
    public async Task CompletingAtTheSourceNeedsTheDelegationAndActMode()
    {
        var h = new Harness();
        var key = WorkItemKey.Format(ConnectorProviders.Microsoft, h.AccountId, WorkItemKind.Task, "l|t");
        var command = new TriageCommand(key, "done", null, true);

        await h.Settings.SaveAsync("t", "alice", Settings(h.AccountId, ["tasks.read"]), default);
        Assert.Equal("delegation_required", (await Assert.ThrowsAsync<DelegationRequiredException>(() =>
            h.Today.DecideAsync("t", "alice", command, default))).Message);
        await h.Settings.SaveAsync("t", "alice",
            Settings(h.AccountId, ["tasks.read", "tasks.complete"]) with { AgentMode = AgentMode.ReadOnly }, default);
        await Assert.ThrowsAsync<DelegationRequiredException>(() => h.Today.DecideAsync("t", "alice", command, default));
        Assert.Empty(h.Provider.Completed);

        await h.Settings.SaveAsync("t", "alice", Settings(h.AccountId, ["tasks.read", "tasks.complete"]), default);
        Assert.True((await h.Today.DecideAsync("t", "alice", command, default)).AppliedToSource);
        Assert.Equal(["l|t"], h.Provider.Completed);
    }

    [Fact]
    public async Task SettingsLinkOnlyTheActorsOwnAccountsAndKeepTheOnboardingDate()
    {
        var h = new Harness();
        var foreign = Guid.NewGuid();
        var service = new SettingsService(h.Settings, h.Store, new FixedClock());
        var input = new UserSettings(null, "08:00", "17:00", Tools,
        [
            new DeclaredAccount(Guid.NewGuid(), "microsoft", AccountUsage.Work, "ACME", h.AccountId, ["mail.read"]),
            new DeclaredAccount(Guid.NewGuid(), "microsoft", AccountUsage.Work, "Altro", foreign, ["mail.read"])
        ], AgentMode.ActWithConfirmation);

        var saved = await service.SaveAsync("t", "alice", input, completeOnboarding: true, default);

        Assert.Equal([h.AccountId, null], saved.Accounts.Select(a => a.ConnectedAccountId));
        Assert.Equal(Now, saved.OnboardingCompletedAt);
        var later = await service.SaveAsync("t", "alice", saved with { OnboardingCompletedAt = null },
            completeOnboarding: false, default);
        Assert.Equal(Now, later.OnboardingCompletedAt);
        Assert.Null(await service.GetAsync("t", "bob", default));
        // A link to the actor's own account of another provider is not trusted either.
        var mismatch = await service.SaveAsync("t", "alice", input with
        {
            Accounts = [new DeclaredAccount(Guid.NewGuid(), "google", AccountUsage.Personal, "Gmail", h.AccountId, [])]
        }, completeOnboarding: false, default);
        Assert.Null(Assert.Single(mismatch.Accounts).ConnectedAccountId);
    }

    [Fact]
    public async Task RecapCountsTheLatestDecisionsPerLocalDay()
    {
        var h = new Harness();
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        h.Triage.Decisions.AddRange(
        [
            new TriageRecord(TriageDecision.Done, new DateTimeOffset(2026, 11, 24, 23, 30, 0, TimeSpan.Zero), null, false),
            new TriageRecord(TriageDecision.Later, new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), Now, false),
            new TriageRecord(TriageDecision.NotDone, new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), null, false),
            new TriageRecord(TriageDecision.Done, new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), null, false)
        ]);

        var recap = await h.Today.RecapAsync("t", "alice", 3, rome, default);

        Assert.Equal([new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25)],
            recap.Select(d => d.Day));
        Assert.Equal((0, 1, 0, 0), (recap[0].Done, recap[0].NotDone, recap[0].Later, recap[0].Dismissed));
        Assert.Equal((0, 0, 1, 0), (recap[1].Done, recap[1].NotDone, recap[1].Later, recap[1].Dismissed));
        Assert.Equal(1, recap[2].Done); // 23:30Z on the 24th is already the 25th in Rome (UTC+1)
        await Assert.ThrowsAsync<ArgumentException>(() => h.Today.RecapAsync("t", "alice", 8, rome, default));
    }

    private static UserSettings Settings(Guid connected, string[] delegations) =>
        new(null, null, null, Tools,
            [new DeclaredAccount(Guid.NewGuid(), "microsoft", AccountUsage.Work, "ACME", connected, delegations)],
            AgentMode.ActWithConfirmation);

    private sealed class Harness
    {
        public readonly Guid AccountId = Guid.NewGuid();
        public readonly InMemorySettings Settings = new();
        public readonly RecordingTriage Triage = new();
        public readonly CapabilityProvider Provider = new();
        public readonly OneAccountStore Store;
        public readonly TodayService Today;

        public Harness()
        {
            Store = new OneAccountStore(AccountId);
            var clock = new FixedClock();
            var connectors = new ConnectorService([Provider], Store, clock,
                new ConnectorSettings(new Uri("https://tao.example.test/connections/callback")));
            Today = new TodayService(connectors, Triage, clock, Settings);
        }
    }

    private sealed class InMemorySettings : ISettingsStore
    {
        private readonly Dictionary<(string, string), UserSettings> _rows = [];
        public Task<UserSettings?> GetAsync(string tenantId, string ownerSubject, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault((tenantId, ownerSubject)));
        public Task SaveAsync(string tenantId, string ownerSubject, UserSettings settings, CancellationToken ct)
        {
            _rows[(tenantId, ownerSubject)] = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTriage : ITriageStore
    {
        public List<TriageRecord> Decisions { get; } = [];
        public Task<IReadOnlyDictionary<string, TriageRecord>> GetAsync(string t, string o,
            IReadOnlyCollection<string> keys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, TriageRecord>>(new Dictionary<string, TriageRecord>());
        public Task SaveAsync(string t, string o, string key, TriageRecord record, CancellationToken ct)
        {
            Decisions.Add(record);
            return Task.CompletedTask;
        }
        public Task AuditAsync(string t, string o, Guid a, string action, string outcome, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<TriageRecord>> RecentAsync(string t, string o, DateTimeOffset since,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TriageRecord>>(Decisions.Where(d => d.DecidedAt >= since).ToArray());
    }

    private sealed class CapabilityProvider : IConnectorProvider
    {
        public List<string[]> Requested { get; } = [];
        public List<string> Completed { get; } = [];
        public string Id => ConnectorProviders.Microsoft;
        public string DisplayName => "Microsoft 365";
        public bool IsConfigured => true;
        public IReadOnlyList<string> Capabilities => ConnectorCapabilities.Microsoft;
        public Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri) => redirectUri;
        public Task<OAuthTokens> ExchangeCodeAsync(string code, string verifier, Uri redirect, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeAsync(OAuthTokens tokens, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
            string accessToken, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> capabilities,
            CancellationToken ct)
        {
            Requested.Add(capabilities.Order().ToArray());
            IReadOnlyList<WorkItem> all =
            [
                new WorkItem(Id, accountId, WorkItemKind.Event, "e", "Standup", StartsAt: Now.AddHours(1)),
                new WorkItem(Id, accountId, WorkItemKind.Mail, "m", "Offerta", Unread: true)
            ];
            return Task.FromResult<IReadOnlyList<WorkItem>>(all
                .Where(i => capabilities.Contains(ConnectorCapabilities.ForKind(i.Kind))).ToArray());
        }

        public Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct)
        {
            Completed.Add(externalTaskId);
            return Task.CompletedTask;
        }
    }

    private sealed class OneAccountStore(Guid accountId) : IConnectorStore
    {
        private readonly ConnectorCredentials _credentials = new(
            new OAuthTokens("access", "refresh", Now.AddHours(1), []), new ConnectedIdentity("me", "Alice", null));
        public Task SaveAuthorizationAsync(string t, string o, string p, byte[] h, string v, DateTimeOffset e,
            CancellationToken ct) => Task.CompletedTask;
        public Task<PendingAuthorization?> ConsumeAuthorizationAsync(string t, string o, byte[] h, CancellationToken ct) =>
            Task.FromResult<PendingAuthorization?>(null);
        public Task<ConnectorAccountSummary> UpsertAccountAsync(string t, string o, string p, ConnectorCredentials c,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConnectorAccountSummary>> ListAccountsAsync(string t, string o, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectorAccountSummary>>(o == "alice"
                ? [new(accountId, ConnectorProviders.Microsoft, "Alice", null, Now, [])] : []);
        public Task<RemovedConnectorAccount?> DeleteAccountAsync(string t, string o, Guid id, CancellationToken ct) =>
            Task.FromResult<RemovedConnectorAccount?>(null);
        public Task<ConnectorCredentials?> UseCredentialsAsync(string t, string o, Guid id,
            Func<ConnectorCredentials, CancellationToken, Task<ConnectorCredentials>> refresh, CancellationToken ct) =>
            Task.FromResult<ConnectorCredentials?>(o == "alice" && id == accountId ? _credentials : null);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
