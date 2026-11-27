using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ReminderDispatchTests
{
    private const string Tenant = "t", Owner = "alice";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Store : IReminderStore, IReminderClaims
    {
        public Dictionary<Guid, Reminder> Rows { get; } = [];
        public List<(Guid Id, DateTimeOffset? Sent, DateTimeOffset? Next)> Completed { get; } = [];
        public Guid? Boom;

        public Task<IReadOnlyList<ClaimedReminder>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ClaimedReminder>>(Rows.Values.Select(r => new ClaimedReminder(Tenant, Owner, r.Id)).ToArray());
        public Task<IReadOnlyList<Reminder>> ListAsync(string t, string o, CancellationToken ct) => Task.FromResult<IReadOnlyList<Reminder>>(Rows.Values.ToArray());
        public Task<Reminder?> GetAsync(string t, string o, Guid id, CancellationToken ct)
        {
            if (id == Boom) throw new InvalidOperationException("database down");
            return Task.FromResult(Rows.GetValueOrDefault(id));
        }
        public Task<Reminder> AddAsync(string t, string o, ReminderDraft d, DateTimeOffset? next, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string t, string o, Guid id, CancellationToken ct) => Task.FromResult(Rows.Remove(id));
        public Task<bool> SetEnabledAsync(string t, string o, Guid id, bool e, DateTimeOffset? n, CancellationToken ct) => Task.FromResult(true);
        public Task CompleteAsync(string t, string o, Guid id, DateTimeOffset? sent, DateTimeOffset? next, CancellationToken ct)
        {
            Completed.Add((id, sent, next));
            return Task.CompletedTask;
        }
    }

    private sealed class Subscriptions(params PushSubscription[] initial) : IPushSubscriptionStore
    {
        public List<PushSubscription> Rows { get; } = [.. initial];
        public List<(Guid Id, bool Success)> Outcomes { get; } = [];
        public Task<IReadOnlyList<PushSubscription>> ListAsync(string t, string o, CancellationToken ct) => Task.FromResult<IReadOnlyList<PushSubscription>>(Rows.ToArray());
        public Task<PushSubscription> UpsertAsync(string t, string o, PushSubscriptionInput i, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string t, string o, Guid id, CancellationToken ct) => Task.FromResult(Rows.RemoveAll(r => r.Id == id) > 0);
        public Task RecordOutcomeAsync(string t, string o, Guid id, bool success, CancellationToken ct) { Outcomes.Add((id, success)); return Task.CompletedTask; }
    }

    private sealed class Consent(bool active) : IConsentGuard
    {
        public Task EnsureActiveAsync(string t, string o, string source, string purpose, CancellationToken ct) =>
            active && source == "push" && purpose == "notifications.push" ? Task.CompletedTask : throw new ConsentRequiredException();
    }

    private sealed class Sender(Func<PushSubscription, PushResult> result) : IWebPushSender
    {
        public List<(PushSubscription To, string Json)> Sent { get; } = [];
        public Task<PushResult> SendAsync(PushSubscription s, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((s, System.Text.Encoding.UTF8.GetString(payload.Span)));
            return Task.FromResult(result(s));
        }
    }

    private static PushSubscription Device(string name) =>
        new(Guid.NewGuid(), $"https://fcm.googleapis.com/fcm/send/{name}", "k", "a", Now.AddDays(-3), 0);

    private static Reminder Daily(string text = "Chiama Marco", DateTimeOffset? due = null) =>
        new(Guid.NewGuid(), text, ReminderRepeat.Daily, "Europe/Rome", new TimeOnly(9, 0), null, true, due ?? Now.AddMinutes(-1), null, Now.AddDays(-5));

    private static (ReminderDispatcher Dispatcher, Store Store, Subscriptions Subs, Sender Sender) Rig(
        Reminder[] reminders, bool consent = true, Func<PushSubscription, PushResult>? result = null, params PushSubscription[] devices)
    {
        var store = new Store();
        foreach (var r in reminders) store.Rows[r.Id] = r;
        var subs = new Subscriptions(devices);
        var sender = new Sender(result ?? (_ => PushResult.Sent));
        return (new ReminderDispatcher(store, store, subs, new Consent(consent), sender, new Clock(Now)), store, subs, sender);
    }

    [Fact]
    public async Task ADueReminderIsPushedToEveryDeviceAndRescheduledForTomorrow()
    {
        var reminder = Daily();
        var (dispatcher, store, subs, sender) = Rig([reminder], devices: [Device("a"), Device("b")]);

        Assert.Equal(1, await dispatcher.DispatchDueAsync(default));

        Assert.Equal(2, sender.Sent.Count);
        using var payload = System.Text.Json.JsonDocument.Parse(sender.Sent[0].Json);
        Assert.Equal("Chiama Marco", payload.RootElement.GetProperty("body").GetString());
        Assert.Equal(reminder.Id.ToString("D"), payload.RootElement.GetProperty("tag").GetString());
        var done = Assert.Single(store.Completed);
        Assert.Equal(Now, done.Sent);
        // 09:00 in Rome on 2026-11-27 is 08:00 UTC, which is "now": the next one is tomorrow.
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero), done.Next);
        Assert.All(subs.Outcomes, o => Assert.True(o.Success));
    }

    [Fact]
    public async Task WithoutTheUsersConsentNothingIsSentButTheScheduleMovesOn()
    {
        var (dispatcher, store, _, sender) = Rig([Daily()], consent: false, devices: [Device("a")]);
        await dispatcher.DispatchDueAsync(default);
        Assert.Empty(sender.Sent);
        var done = Assert.Single(store.Completed);
        Assert.Null(done.Sent);
        Assert.NotNull(done.Next);
    }

    [Fact]
    public async Task ADeviceThatIsGoneIsForgottenAndATemporaryFailureIsCounted()
    {
        var gone = Device("gone");
        var flaky = Device("flaky");
        var (dispatcher, _, subs, _) = Rig([Daily()], result: s => s.Id == gone.Id ? PushResult.Gone : PushResult.Failed, devices: [gone, flaky]);
        await dispatcher.DispatchDueAsync(default);
        Assert.DoesNotContain(subs.Rows, r => r.Id == gone.Id);
        Assert.Equal([(flaky.Id, false)], subs.Outcomes);
    }

    [Fact]
    public async Task AReminderThatIsMuchTooLateIsNotWorthANotification()
    {
        var (dispatcher, store, _, sender) = Rig([Daily(due: Now.AddHours(-7))], devices: [Device("a")]);
        await dispatcher.DispatchDueAsync(default);
        Assert.Empty(sender.Sent);
        Assert.Null(Assert.Single(store.Completed).Sent);
    }

    [Fact]
    public async Task AOneOffReminderEndsAfterItsRun()
    {
        var once = new Reminder(Guid.NewGuid(), "Dentista", ReminderRepeat.Once, "Europe/Rome", new TimeOnly(8, 59),
            new DateOnly(2026, 11, 27), true, Now.AddMinutes(-1), null, Now.AddDays(-5));
        var (dispatcher, store, _, sender) = Rig([once], devices: [Device("a")]);
        await dispatcher.DispatchDueAsync(default);
        Assert.Single(sender.Sent);
        Assert.Null(Assert.Single(store.Completed).Next);
    }

    [Fact]
    public async Task OneReminderFailingDoesNotStopTheOthers()
    {
        var bad = Daily("rotto");
        var good = Daily("buono");
        var (dispatcher, store, _, sender) = Rig([bad, good], devices: [Device("a")]);
        store.Boom = bad.Id;
        Assert.Equal(1, await dispatcher.DispatchDueAsync(default));
        Assert.Single(sender.Sent);
        Assert.Equal(good.Id, Assert.Single(store.Completed).Id);
    }

    [Fact]
    public async Task ADeletedReminderIsSimplyIgnored()
    {
        var (dispatcher, store, _, sender) = Rig([Daily()], devices: [Device("a")]);
        store.Rows.Clear();
        await dispatcher.DispatchDueAsync(default);
        Assert.Empty(sender.Sent);
    }
}

public sealed class PushServiceTests
{
    private sealed class Subs : IPushSubscriptionStore
    {
        public List<PushSubscriptionInput> Added { get; } = [];
        public Task<IReadOnlyList<PushSubscription>> ListAsync(string t, string o, CancellationToken ct) => Task.FromResult<IReadOnlyList<PushSubscription>>([]);
        public Task<PushSubscription> UpsertAsync(string t, string o, PushSubscriptionInput i, CancellationToken ct)
        {
            Added.Add(i);
            return Task.FromResult(new PushSubscription(Guid.NewGuid(), i.Endpoint, i.P256dh, i.Auth, DateTimeOffset.UtcNow, 0));
        }
        public Task<bool> RemoveAsync(string t, string o, Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task RecordOutcomeAsync(string t, string o, Guid id, bool success, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Consent(bool active) : IConsentGuard
    {
        public Task EnsureActiveAsync(string t, string o, string source, string purpose, CancellationToken ct) =>
            active ? Task.CompletedTask : throw new ConsentRequiredException();
    }

    private sealed class NoSender : IWebPushSender
    {
        public Task<PushResult> SendAsync(PushSubscription s, ReadOnlyMemory<byte> p, CancellationToken ct) => Task.FromResult(PushResult.Sent);
    }

    private sealed class Key : IVapidPublicKey
    {
        public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("pub");
    }

    private static string K(int n) => PushSubscriptionRules.Encode(new byte[n].Select((_, i) => i == 0 ? (byte)4 : (byte)1).ToArray());

    [Fact]
    public async Task SubscribingNeedsConsentAndAKnownPushService()
    {
        var store = new Subs();
        var denied = new PushService(store, new Consent(false), new NoSender(), new Key());
        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            denied.SubscribeAsync("t", "o", "https://fcm.googleapis.com/fcm/send/x", K(65), K(16), default));
        Assert.Empty(store.Added);

        var allowed = new PushService(store, new Consent(true), new NoSender(), new Key());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            allowed.SubscribeAsync("t", "o", "https://169.254.169.254/latest", K(65), K(16), default));
        Assert.Empty(store.Added);
        await allowed.SubscribeAsync("t", "o", "https://fcm.googleapis.com/fcm/send/x", K(65), K(16), default);
        Assert.Single(store.Added);
    }
}
