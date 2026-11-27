using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure.Outbound;

namespace Tao.Infrastructure.Push;

public sealed record PushOptions(string Subject);

/// <summary>Posts an encrypted message to the push service of a subscription through the SSRF-guarded client.</summary>
public sealed class WebPushSender(VapidKeyProvider vapid, OutboundClient client, PushOptions options, TimeProvider time) : IWebPushSender
{
    public async Task<PushResult> SendAsync(PushSubscription subscription, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        try
        {
            var endpoint = PushSubscriptionRules.Validate(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            var keys = await vapid.KeysAsync(ct);
            var body = WebPushEncryption.Encrypt(payload.Span, PushSubscriptionRules.Decode(subscription.P256dh)!,
                PushSubscriptionRules.Decode(subscription.Auth)!);
            var response = await client.SendAsync(new OutboundRequest(HttpMethod.Post, endpoint.Endpoint,
                new Dictionary<string, string>
                {
                    ["Authorization"] = keys.AuthorizationHeader(new Uri(endpoint.Endpoint), options.Subject, time.GetUtcNow()),
                    ["Content-Encoding"] = "aes128gcm",
                    ["TTL"] = "86400",
                    ["Urgency"] = "normal"
                }, BinaryBody: body), ct, maxBytes: 16 * 1024);
            return response.Status is >= 200 and < 300 ? PushResult.Sent
                : response.Status is 404 or 410 ? PushResult.Gone : PushResult.Failed;
        }
        catch (OutboundBlockedException) { return PushResult.Failed; }
        catch (ArgumentException) { return PushResult.Failed; }
    }
}
