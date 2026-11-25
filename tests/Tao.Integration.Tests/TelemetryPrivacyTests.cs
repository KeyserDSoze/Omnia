using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

public sealed class TelemetryPrivacyTests
{
    [Fact]
    public void ExportedSpansLoseQueryStringsFullUrlsAndClientAddressesButKeepThePath()
    {
        var exported = new List<Dictionary<string, object?>>();
        using var source = new ActivitySource("tao-privacy-test");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("tao-privacy-test")
            .AddProcessor(new TelemetryPrivacyProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(new CollectingExporter(exported)))
            .Build();

        using (var span = source.StartActivity("GET /connections/callback"))
        {
            span!.SetTag("url.path", "/api/v1/today");
            span.SetTag("url.query", "?code=SECRET-CODE&state=abc");
            span.SetTag("url.full", "https://api.example.test/api/v1/today?code=SECRET-CODE");
            span.SetTag("client.address", "203.0.113.7");
            span.SetTag("http.response.status_code", 200);
        }

        var tags = Assert.Single(exported);
        Assert.Equal("/api/v1/today", tags["url.path"]);
        Assert.Equal(200, tags["http.response.status_code"]);
        Assert.DoesNotContain(tags.Keys, key => key is "url.query" or "url.full" or "client.address");
        Assert.DoesNotContain(tags.Values, value => value?.ToString()?.Contains("SECRET-CODE") == true);
    }

    private sealed class CollectingExporter(List<Dictionary<string, object?>> sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                sink.Add(activity.TagObjects.ToDictionary(t => t.Key, t => t.Value));
            return ExportResult.Success;
        }
    }
}
