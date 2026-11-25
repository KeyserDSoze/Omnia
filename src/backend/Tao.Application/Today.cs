using Tao.Domain;

namespace Tao.Application;

public sealed record TriageRecord(TriageDecision Decision, DateTimeOffset DecidedAt,
    DateTimeOffset? SnoozedUntil, bool AppliedToSource);

public sealed record TodayItem(string Key, WorkItem Item, int Priority, TriageRecord? Triage);

/// <summary>Per-account outcome: "ok", "reconnect_required" or "unavailable" (never hidden).</summary>
public sealed record AccountStatus(Guid AccountId, string Provider, string DisplayName, string Status);

public sealed record TodaySnapshot(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TodayItem> Items,
    IReadOnlyList<AccountStatus> Accounts);

public sealed record TriageCommand(string Key, string Decision, int? SnoozeMinutes, bool ApplyToSource);

public sealed record TriageResult(string Key, string Decision, DateTimeOffset? SnoozedUntil,
    bool AppliedToSource);

/// <summary>Actor-scoped triage decisions and the audit trail of actions run on providers.</summary>
public interface ITriageStore
{
    Task<IReadOnlyDictionary<string, TriageRecord>> GetAsync(string tenantId, string ownerSubject,
        IReadOnlyCollection<string> keys, CancellationToken ct);
    Task SaveAsync(string tenantId, string ownerSubject, string key, TriageRecord record, CancellationToken ct);
    Task AuditAsync(string tenantId, string ownerSubject, Guid accountId, string action, string outcome,
        CancellationToken ct);
}

/// <summary>
/// The daily deck: live work signals from every connected account, ordered by deterministic
/// priority, with the user's swipe decisions (done / not done / later / dismissed).
/// </summary>
public sealed class TodayService(ConnectorService connectors, ITriageStore triage, TimeProvider time)
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(72);
    public const int DefaultSnoozeMinutes = 120;

    public async Task<TodaySnapshot> GetAsync(string tenantId, string ownerSubject, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        if (to <= from || to - from > MaxWindow)
            throw new ArgumentException("invalid_window");
        var accounts = await connectors.ListAccountsAsync(tenantId, ownerSubject, ct);
        var fetched = await Task.WhenAll(accounts.Select(async account =>
        {
            try
            {
                var (provider, credentials) = await connectors.AccessAsync(tenantId, ownerSubject, account, ct);
                var items = await provider.GetWorkItemsAsync(account.Id, credentials.Identity,
                    credentials.Tokens.AccessToken, from, to, ct);
                return (Status: new AccountStatus(account.Id, account.Provider, account.DisplayName, "ok"), Items: items);
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
}
