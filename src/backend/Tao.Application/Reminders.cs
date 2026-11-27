using System.Text.Json;
using Tao.Domain;

namespace Tao.Application;

public sealed record Reminder(Guid Id, string Text, ReminderRepeat Repeat, string TimeZone, TimeOnly At, DateOnly? Date,
    bool Enabled, DateTimeOffset? NextRunAt, DateTimeOffset? LastSentAt, DateTimeOffset CreatedAt);

public sealed record ClaimedReminder(string TenantId, string OwnerSubject, Guid Id);

public sealed class TooManyRemindersException() : Exception("too_many_reminders");

/// <summary>Actor-scoped storage of reminders; the text is sealed at rest.</summary>
public interface IReminderStore
{
    Task<IReadOnlyList<Reminder>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct);
    Task<Reminder?> GetAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct);
    /// <summary>Throws <see cref="TooManyRemindersException"/> past <see cref="ReminderRules.MaxPerActor"/>.</summary>
    Task<Reminder> AddAsync(string tenantId, string ownerSubject, ReminderDraft draft, DateTimeOffset? next, CancellationToken ct);
    Task<bool> DeleteAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct);
    Task<bool> SetEnabledAsync(string tenantId, string ownerSubject, Guid id, bool enabled, DateTimeOffset? next, CancellationToken ct);
    /// <summary>After a run: records it, schedules the next one and releases the claim.</summary>
    Task CompleteAsync(string tenantId, string ownerSubject, Guid id, DateTimeOffset? sentAt, DateTimeOffset? next, CancellationToken ct);
}

/// <summary>Cross-actor view of the reminders that are due: identifiers only, through the restricted worker role.</summary>
public interface IReminderClaims
{
    Task<IReadOnlyList<ClaimedReminder>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken ct);
}

public sealed class ReminderService(IReminderStore store, TimeProvider time)
{
    public Task<IReadOnlyList<Reminder>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct) =>
        store.ListAsync(tenantId, ownerSubject, ct);

    public async Task<Reminder> CreateAsync(string tenantId, string ownerSubject, string? text, string? repeat, string? at,
        string? date, string? timeZone, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var draft = ReminderRules.Validate(text, repeat, at, date, timeZone, now);
        return await store.AddAsync(tenantId, ownerSubject, draft, ReminderRules.Next(draft, now), ct);
    }

    public Task<bool> DeleteAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct) =>
        store.DeleteAsync(tenantId, ownerSubject, id, ct);

    /// <summary>Turning a reminder on schedules its next run from now; a one-off whose time has passed stays off.</summary>
    public async Task<bool> SetEnabledAsync(string tenantId, string ownerSubject, Guid id, bool enabled, CancellationToken ct)
    {
        var reminder = await store.GetAsync(tenantId, ownerSubject, id, ct);
        if (reminder is null) return false;
        var next = enabled
            ? ReminderRules.Next(reminder.Repeat, reminder.At, reminder.Date,
                ReminderRules.TryZone(reminder.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc, time.GetUtcNow())
            : null;
        return await store.SetEnabledAsync(tenantId, ownerSubject, id, enabled, next, ct);
    }
}

/// <summary>Sends the reminders that are due as push notifications to the user's own devices (ADR 0113).</summary>
public sealed class ReminderDispatcher(IReminderClaims claims, IReminderStore reminders, IPushSubscriptionStore subscriptions,
    IConsentGuard consent, IWebPushSender sender, TimeProvider time)
{
    public const int BatchSize = 50;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    /// <summary>A reminder this much later than planned (the service was down) is not worth a notification.</summary>
    public static readonly TimeSpan MaxLateness = TimeSpan.FromHours(6);

    /// <summary>How many reminders were processed; one failing reminder never stops the others.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var processed = 0;
        foreach (var claimed in await claims.ClaimDueAsync(BatchSize, Lease, ct))
        {
            try { await DispatchAsync(claimed, ct); processed++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* the claim expires and the reminder is tried again */ }
        }
        return processed;
    }

    private async Task DispatchAsync(ClaimedReminder claimed, CancellationToken ct)
    {
        var (tenant, owner) = (claimed.TenantId, claimed.OwnerSubject);
        var reminder = await reminders.GetAsync(tenant, owner, claimed.Id, ct);
        if (reminder is null) return;
        var now = time.GetUtcNow();
        var zone = ReminderRules.TryZone(reminder.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var next = ReminderRules.Next(reminder.Repeat, reminder.At, reminder.Date, zone, now);
        var late = reminder.NextRunAt is { } due && now - due > MaxLateness;
        DateTimeOffset? sentAt = null;
        if (!late && await TryNotifyAsync(tenant, owner, reminder, ct)) sentAt = now;
        await reminders.CompleteAsync(tenant, owner, reminder.Id, sentAt, next, ct);
    }

    private async Task<bool> TryNotifyAsync(string tenant, string owner, Reminder reminder, CancellationToken ct)
    {
        try { await consent.EnsureActiveAsync(tenant, owner, PushNotificationSource.Name, ConsentGrant.PushNotifications, ct); }
        catch (ConsentRequiredException) { return false; }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = "Tao", body = reminder.Text, tag = reminder.Id.ToString("D"), url = "./"
        });
        var delivered = false;
        foreach (var subscription in await subscriptions.ListAsync(tenant, owner, ct))
        {
            var result = await sender.SendAsync(subscription, payload, ct);
            if (result == PushResult.Gone) await subscriptions.RemoveAsync(tenant, owner, subscription.Id, ct);
            else await subscriptions.RecordOutcomeAsync(tenant, owner, subscription.Id, result == PushResult.Sent, ct);
            delivered |= result == PushResult.Sent;
        }
        return delivered;
    }
}
