using System.Diagnostics;
using OpenTelemetry;

namespace Tao.Api;

/// <summary>
/// Application Insights receives the shape of the traffic, never its content: query strings (OAuth codes,
/// cursors), full URLs and client addresses are removed from every span before export.
/// </summary>
public sealed class TelemetryPrivacyProcessor : BaseProcessor<Activity>
{
    private static readonly string[] Removed =
        ["url.query", "url.full", "http.url", "http.target", "client.address", "http.client_ip", "user_agent.original"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in Removed) activity.SetTag(tag, null);
    }
}
