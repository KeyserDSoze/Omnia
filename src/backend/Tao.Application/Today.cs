using Tao.Domain;

namespace Tao.Application;

public sealed record TriageRecord(TriageDecision Decision, DateTimeOffset DecidedAt,
    DateTimeOffset? SnoozedUntil, bool AppliedToSource);

public sealed record TodayItem(string Key, WorkItem Item, int Priority, TriageRecord? Triage);

/// <summary>Per-account outcome: "ok", "paused" (no read delegation), "reconnect_required" or "unavailable".</summary>
public sealed record AccountStatus(Guid AccountId, string Provider, string DisplayName, string Status);

public sealed record TodaySnapshot(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TodayItem> Items,
    IReadOnlyList<AccountStatus> Accounts);

public sealed record TriageCommand(string Key, string Decision, int? SnoozeMinutes, bool ApplyToSource);

public sealed record TriageResult(string Key, string Decision, DateTimeOffset? SnoozedUntil,
    bool AppliedToSource);

/// <summary>Decision counts of one local day (latest decision per card), used for the chat's starting hints.</summary>
public sealed record DayRecap(DateOnly Day, int Done, int NotDone, int Later, int Dismissed);

/// <summary>Actor-scoped triage decisions and the audit trail of actions run on providers.</summary>
public interface ITriageStore
{
    Task<IReadOnlyDictionary<string, TriageRecord>> GetAsync(string tenantId, string ownerSubject,
        IReadOnlyCollection<string> keys, CancellationToken ct);
    Task SaveAsync(string tenantId, string ownerSubject, string key, TriageRecord record, CancellationToken ct);
    Task AuditAsync(string tenantId, string ownerSubject, Guid accountId, string action, string outcome,
        CancellationToken ct);
    /// <summary>Decisions taken since <paramref name="since"/> (bounded); keys and content are not returned.</summary>
    Task<IReadOnlyList<TriageRecord>> RecentAsync(string tenantId, string ownerSubject, DateTimeOffset since,
        CancellationToken ct);
}

/// <summary>
/// The daily deck: live work signals from every connected account, ordered by deterministic
/// priority, with the user's swipe decisions (done / not done / later / dismissed).
/// </summary>
public sealed class TodayService(ConnectorService connectors, ITriageStore triage, TimeProvider time,
    ISettingsStore? settingsStore = null)
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(72);
    public const int DefaultSnoozeMinutes = 120;
    public const int MaxRecapDays = 7;

    public async Task<TodaySnapshot> GetAsync(string tenantId, string ownerSubject, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        if (to <= from || to - from > MaxWindow)
            throw new ArgumentException("invalid_window");
        var accounts = await connectors.ListAccountsAsync(tenantId, ownerSubject, ct);
        var settings = await SettingsAsync(tenantId, ownerSubject, ct);
        var fetched = await Task.WhenAll(accounts.Select(async account =>
        {
            var granted = UserSettings.Granted(settings, account.Id, account.Provider);
            // Data minimization: an account with no read delegation is not even contacted.
            if (!granted.Any(ConnectorCapabilities.IsRead))
                return (Status: new AccountStatus(account.Id, account.Provider, account.DisplayName, "paused"),
                    Items: (IReadOnlyList<WorkItem>)[]);
            try
            {
                var (provider, credentials) = await connectors.AccessAsync(tenantId, ownerSubject, account, ct);
                var items = await provider.GetWorkItemsAsync(account.Id, credentials.Identity,
                    credentials.Tokens.AccessToken, from, to, granted, ct);
                return (Status: new AccountStatus(account.Id, account.Provider, account.DisplayName, "ok"),
                    Items: (IReadOnlyList<WorkItem>)items
                        .Where(i => granted.Contains(ConnectorCapabilities.ForKind(i.Kind))).ToArray());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var status = e is ConnectorUnavailableException { Code: "reconnect_required" }
                    ? "reconnect_required" : "unavailable";
                return (Status: new AccountStatus(account.Id, account.Provider, account.DisplayName, status),
                    Items: (IReadOnlyList<WorkItem>)[]);
            }
        }));

        var items = fetched.SelectMany(x => x.Items).GroupBy(x => x.Key).Select(g => g.First()).ToArray();
        var decisions = items.Length == 0
            ? new Dictionary<string, TriageRecord>()
            : await triage.GetAsync(tenantId, ownerSubject, items.Select(x => x.Key).ToArray(), ct);
        var now = time.GetUtcNow();
        var ordered = items
            .Select(item => new TodayItem(item.Key, item, WorkItemPriority.Score(item, now),
                decisions.GetValueOrDefault(item.Key)))
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Item.StartsAt ?? x.Item.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        return new TodaySnapshot(from, to, ordered, fetched.Select(x => x.Status).ToArray());
    }

    public async Task<TriageResult> DecideAsync(string tenantId, string ownerSubject, TriageCommand command,
        CancellationToken ct)
    {
        if (!WorkItemKey.TryParse(command.Key, out var provider, out var accountId, out var kind,
                out var externalId))
            throw new ArgumentException("invalid_item");
        if (!TriageDecisions.TryParse(command.Decision, out var decision))
            throw new ArgumentException("invalid_decision");
        if (command.SnoozeMinutes is < 15 or > 1440)
            throw new ArgumentException("invalid_snooze");
        if (command.ApplyToSource && (decision != TriageDecision.Done || kind != WorkItemKind.Task))
            throw new ArgumentException("apply_not_supported");
        var account = (await connectors.ListAccountsAsync(tenantId, ownerSubject, ct))
            .FirstOrDefault(a => a.Id == accountId && a.Provider == provider)
            ?? throw new KeyNotFoundException("connector_account_not_found");
        if (command.ApplyToSource && !UserSettings.Granted(await SettingsAsync(tenantId, ownerSubject, ct),
                account.Id, account.Provider).Contains(ConnectorCapabilities.TasksComplete))
            throw new DelegationRequiredException();

        var now = time.GetUtcNow();
        var applied = false;
        if (command.ApplyToSource)
        {
            var (connector, credentials) = await connectors.AccessAsync(tenantId, ownerSubject, account, ct);
            try
            {
                await connector.CompleteTaskAsync(credentials.Tokens.AccessToken, externalId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await triage.AuditAsync(tenantId, ownerSubject, account.Id, "task.complete", "failed", ct);
                throw new ConnectorUnavailableException("apply_failed", e);
            }
            await triage.AuditAsync(tenantId, ownerSubject, account.Id, "task.complete", "succeeded", ct);
            applied = true;
        }

        DateTimeOffset? snooze = decision == TriageDecision.Later
            ? now.AddMinutes(command.SnoozeMinutes ?? DefaultSnoozeMinutes) : null;
        await triage.SaveAsync(tenantId, ownerSubject, command.Key,
            new TriageRecord(decision, now, snooze, applied), ct);
        return new TriageResult(command.Key, TriageDecisions.ToWire(decision), snooze, applied);
    }

    /// <summary>Counts of the latest decisions for the last <paramref name="days"/> local days (today included).</summary>
    public async Task<IReadOnlyList<DayRecap>> RecapAsync(string tenantId, string ownerSubject, int days,
        TimeZoneInfo timeZone, CancellationToken ct)
    {
        if (days is < 1 or > MaxRecapDays) throw new ArgumentException("invalid_window");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), timeZone).DateTime);
        var first = today.AddDays(1 - days);
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue),
            timeZone.GetUtcOffset(first.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var records = await triage.RecentAsync(tenantId, ownerSubject, since, ct);
        var byDay = records.GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.DecidedAt, timeZone).DateTime))
            .ToDictionary(g => g.Key, g => g.ToArray());
        return Enumerable.Range(0, days).Select(offset =>
        {
            var day = first.AddDays(offset);
            var list = byDay.GetValueOrDefault(day) ?? [];
            return new DayRecap(day, list.Count(r => r.Decision == TriageDecision.Done),
                list.Count(r => r.Decision == TriageDecision.NotDone), list.Count(r => r.Decision == TriageDecision.Later),
                list.Count(r => r.Decision == TriageDecision.Dismissed));
        }).ToArray();
    }

    private Task<UserSettings?> SettingsAsync(string tenantId, string ownerSubject, CancellationToken ct) =>
        settingsStore?.GetAsync(tenantId, ownerSubject, ct) ?? Task.FromResult<UserSettings?>(null);
}
