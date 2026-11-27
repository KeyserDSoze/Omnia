using System.Text.Json;
using Tao.Domain;

namespace Tao.Application;

public static class PushNotificationSource
{
    public const string Name = "push";
}

public sealed record PushSubscription(Guid Id, string Endpoint, string P256dh, string Auth, DateTimeOffset CreatedAt, int Failures);

public enum PushResult { Sent, Gone, Failed }

/// <summary>Actor-scoped storage of the user's own browsers; endpoint and keys are sealed at rest.</summary>
public interface IPushSubscriptionStore
{
    Task<IReadOnlyList<PushSubscription>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct);
    /// <summary>Re-subscribing the same endpoint updates it; past the cap the oldest one is dropped.</summary>
    Task<PushSubscription> UpsertAsync(string tenantId, string ownerSubject, PushSubscriptionInput input, CancellationToken ct);
    Task<bool> RemoveAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct);
    Task RecordOutcomeAsync(string tenantId, string ownerSubject, Guid id, bool success, CancellationToken ct);
}

public interface IWebPushSender
{
    Task<PushResult> SendAsync(PushSubscription subscription, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>The public half of the server's VAPID key: what a browser needs to subscribe.</summary>
public interface IVapidPublicKey
{
    Task<string> GetAsync(CancellationToken ct);
}

/// <summary>Subscribing needs the user's dedicated consent; the browser permission is a second, separate gate.</summary>
public sealed class PushService(IPushSubscriptionStore store, IConsentGuard consent, IWebPushSender sender, IVapidPublicKey vapid)
{
    public Task<string> PublicKeyAsync(CancellationToken ct) => vapid.GetAsync(ct);

    public Task<IReadOnlyList<PushSubscription>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct) =>
        store.ListAsync(tenantId, ownerSubject, ct);

    public async Task<PushSubscription> SubscribeAsync(string tenantId, string ownerSubject, string? endpoint, string? p256dh,
        string? auth, CancellationToken ct)
    {
        var input = PushSubscriptionRules.Validate(endpoint, p256dh, auth);
        await consent.EnsureActiveAsync(tenantId, ownerSubject, PushNotificationSource.Name, ConsentGrant.PushNotifications, ct);
        return await store.UpsertAsync(tenantId, ownerSubject, input, ct);
    }

    public Task<bool> UnsubscribeAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct) =>
        store.RemoveAsync(tenantId, ownerSubject, id, ct);

    /// <summary>A test notification to every device of the user; returns how many were delivered.</summary>
    public async Task<int> SendTestAsync(string tenantId, string ownerSubject, CancellationToken ct)
    {
        await consent.EnsureActiveAsync(tenantId, ownerSubject, PushNotificationSource.Name, ConsentGrant.PushNotifications, ct);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title = "Tao", body = "Notifications work.", tag = "test", url = "./" });
        var delivered = 0;
        foreach (var subscription in await store.ListAsync(tenantId, ownerSubject, ct))
        {
            var result = await sender.SendAsync(subscription, payload, ct);
            if (result == PushResult.Gone) await store.RemoveAsync(tenantId, ownerSubject, subscription.Id, ct);
            else await store.RecordOutcomeAsync(tenantId, ownerSubject, subscription.Id, result == PushResult.Sent, ct);
            if (result == PushResult.Sent) delivered++;
        }
        return delivered;
    }
}
