using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// End-to-end API contract of the connectors and the daily deck: real PostgreSQL (restricted
/// RLS role), real sealing, fixture authentication and a scripted provider (no network).
/// </summary>
public sealed class ApiConnectorTests
{
    [Fact]
    public async Task ConnectTriageApplyAndDisconnectThroughTheApi()
    {
        var provider = new ScriptedProvider();
        using var environment = new TemporaryEnvironment(Settings().ToDictionary(
            x => x.Key.Replace(":", "__"), x => x.Value));
        using var host = Host(provider);
        using var http = host.CreateClient();
        var tenant = "api-conn-" + Guid.NewGuid().ToString("N");

        using (var anonymous = await http.GetAsync("/api/v1/connectors"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using (var list = await Send(http, HttpMethod.Get, "/api/v1/connectors", tenant))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
            using var json = await Json(list);
            var microsoft = Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
            Assert.True(microsoft.GetProperty("configured").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("accounts").EnumerateArray());
        }
        using (var unknown = await Send(http, HttpMethod.Post, "/api/v1/connectors/dropbox/authorize", tenant, new { }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var google = await Send(http, HttpMethod.Post, "/api/v1/connectors/google/authorize", tenant, new { }))
            Assert.Equal(HttpStatusCode.Conflict, google.StatusCode);

        string state;
        using (var start = await Send(http, HttpMethod.Post, "/api/v1/connectors/microsoft/authorize", tenant, new { }))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            using var json = await Json(start);
            var url = new Uri(json.RootElement.GetProperty("authorizationUrl").GetString()!);
            state = HttpUtility.ParseQueryString(url.Query)["state"]!;
            Assert.Equal(new Uri("https://tao.example.test/connections/callback"), provider.Redirect);
        }

        using (var foreign = await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant,
            new { state, code = "code-1" }, subject: "mallory"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
            using var json = await Json(foreign);
            Assert.Equal("unknown_state", json.RootElement.GetProperty("error").GetString());
        }
        Guid accountId;
        using (var complete = await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant,
            new { state, code = "code-1" }))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            using var json = await Json(complete);
            accountId = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Alice Work", json.RootElement.GetProperty("displayName").GetString());
            Assert.False(json.RootElement.TryGetProperty("tokens", out _));
        }
        using (var replay = await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant,
            new { state, code = "code-1" }))
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var taskKey = WorkItemKey.Format(ConnectorProviders.Microsoft, accountId, WorkItemKind.Task, "L|T1");
        var mailKey = WorkItemKey.Format(ConnectorProviders.Microsoft, accountId, WorkItemKind.Mail, "m1");
        using (var today = await Send(http, HttpMethod.Get, "/api/v1/today", tenant))
        {
            Assert.Equal(HttpStatusCode.OK, today.StatusCode);
            using var json = await Json(today);
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal([taskKey, mailKey], items.Select(i => i.GetProperty("key").GetString()));
            Assert.Equal("task", items[0].GetProperty("item").GetProperty("kind").GetString());
            Assert.Equal("ok", Assert.Single(json.RootElement.GetProperty("accounts").EnumerateArray())
                .GetProperty("status").GetString());
        }
        using (var others = await Send(http, HttpMethod.Get, "/api/v1/today", tenant, subject: "bob"))
        {
            using var json = await Json(others);
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }

        using (var notSupported = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = mailKey, decision = "done", applyToSource = true }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notSupported.StatusCode);
            using var json = await Json(notSupported);
            Assert.Equal("apply_not_supported", json.RootElement.GetProperty("error").GetString());
        }
        using (var foreignDecision = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = taskKey, decision = "done", applyToSource = true }, subject: "bob"))
            Assert.Equal(HttpStatusCode.NotFound, foreignDecision.StatusCode);
        Assert.Empty(provider.Completed);

        using (var done = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = taskKey, decision = "done", applyToSource = true }))
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            using var json = await Json(done);
            Assert.True(json.RootElement.GetProperty("appliedToSource").GetBoolean());
        }
        Assert.Equal(["L|T1"], provider.Completed);
        using (var later = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = mailKey, decision = "later", snoozeMinutes = 30 }))
            Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        using (var today = await Send(http, HttpMethod.Get, "/api/v1/today", tenant))
        {
            using var json = await Json(today);
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal("done", items[0].GetProperty("triage").GetProperty("decision").GetString());
            Assert.Equal("later", items[1].GetProperty("triage").GetProperty("decision").GetString());
        }

        using (var foreignDelete = await Send(http, HttpMethod.Delete, $"/api/v1/connectors/accounts/{accountId}",
            tenant, subject: "bob"))
            Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        using (var delete = await Send(http, HttpMethod.Delete, $"/api/v1/connectors/accounts/{accountId}", tenant))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(1, provider.Revoked);

        // Erasure revokes and deletes every connected account of the actor.
        using (var again = await Send(http, HttpMethod.Post, "/api/v1/connectors/microsoft/authorize", tenant, new { }))
        {
            using var json = await Json(again);
            state = HttpUtility.ParseQueryString(
                new Uri(json.RootElement.GetProperty("authorizationUrl").GetString()!).Query)["state"]!;
        }
        using (await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant, new { state, code = "c2" })) { }
        using (var erase = await Send(http, HttpMethod.Post, "/api/v1/privacy/erase", tenant,
            new { confirmation = "ERASE_MY_DATA" }))
        {
            Assert.Equal(HttpStatusCode.OK, erase.StatusCode);
            using var json = await Json(erase);
            Assert.Equal(1, json.RootElement.GetProperty("deletedConnectorAccounts").GetInt32());
            Assert.Equal(2, provider.Revoked);
        }
        using (var list = await Send(http, HttpMethod.Get, "/api/v1/connectors", tenant))
        {
            using var json = await Json(list);
            Assert.Empty(json.RootElement.GetProperty("accounts").EnumerateArray());
        }
    }

    [Fact]
    public async Task BriefingRequiresItsOwnConsentAndAConfiguredModel()
    {
        var provider = new ScriptedProvider();
        var model = new ScriptedModel();
        using var environment = new TemporaryEnvironment(Settings().ToDictionary(
            x => x.Key.Replace(":", "__"), x => x.Value));
        using var host = Host(provider, model);
        using var http = host.CreateClient();
        var tenant = "api-brief-" + Guid.NewGuid().ToString("N");

        using (var info = await Send(http, HttpMethod.Get, "/api/v1/assistant", tenant))
        {
            using var json = await Json(info);
            Assert.True(json.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal("scripted-model", json.RootElement.GetProperty("model").GetString());
        }
        string state;
        using (var start = await Send(http, HttpMethod.Post, "/api/v1/connectors/microsoft/authorize", tenant, new { }))
        {
            using var json = await Json(start);
            state = HttpUtility.ParseQueryString(
                new Uri(json.RootElement.GetProperty("authorizationUrl").GetString()!).Query)["state"]!;
        }
        using (await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant, new { state, code = "c" })) { }

        var briefing = new { timeZone = "Europe/Rome" };
        using (var denied = await Send(http, HttpMethod.Post, "/api/v1/today/briefing", tenant, briefing))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var json = await Json(denied);
            Assert.Equal("assistant_consent_required", json.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(0, model.Calls);
        // Memory capture consent does not authorize the assistant.
        using (await Send(http, HttpMethod.Post, "/api/v1/consents", tenant, new
        {
            source = "manual", purpose = "memory.capture", policyVersion = "privacy-v1",
            expiresAt = DateTimeOffset.UtcNow.AddDays(3), accepted = true
        })) { }
        using (var stillDenied = await Send(http, HttpMethod.Post, "/api/v1/today/briefing", tenant, briefing))
            Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);

        using (var grant = await Send(http, HttpMethod.Post, "/api/v1/consents", tenant, new
        {
            source = "assistant", purpose = "assistant.briefing", policyVersion = "assistant-v1",
            expiresAt = DateTimeOffset.UtcNow.AddDays(30), accepted = true
        }))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using (var badZone = await Send(http, HttpMethod.Post, "/api/v1/today/briefing", tenant,
            new { timeZone = "../../etc/passwd" }))
            Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
        using (var ok = await Send(http, HttpMethod.Post, "/api/v1/today/briefing", tenant, briefing))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var json = await Json(ok);
            Assert.Equal("Prima il preventivo.", json.RootElement.GetProperty("summary").GetString());
            var suggestion = Assert.Single(json.RootElement.GetProperty("suggestions").EnumerateArray());
            Assert.EndsWith(":task:L|T1", suggestion.GetProperty("key").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("cardsSent").GetInt32());
        }
        Assert.DoesNotContain("alice@work.example", model.LastPrompt);

        model.Configured = false;
        using (var off = await Send(http, HttpMethod.Post, "/api/v1/today/briefing", tenant, briefing))
        {
            Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
            using var json = await Json(off);
            Assert.Equal("assistant_not_configured", json.RootElement.GetProperty("error").GetString());
        }
    }

    private sealed class ScriptedModel : IChatModel
    {
        private const string Reply =
            """{"summary":"Prima il preventivo.","suggestions":[{"ref":"c1","preAction":"Apri il listino."}]}""";
        public bool Configured { get; set; } = true;
        public int Calls;
        public string LastPrompt { get; private set; } = "";
        public bool IsConfigured => Configured;
        public string Model => "scripted-model";

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastPrompt = string.Join("\n", messages.Select(m => m.Content));
            return Task.FromResult(Reply);
        }
    }

    [Fact]
    public async Task GuidedSettingsDriveWhatTheAgentReadsAndDoesOnBehalfOfTheUser()
    {
        var provider = new ScriptedProvider();
        using var environment = new TemporaryEnvironment(Settings().ToDictionary(
            x => x.Key.Replace(":", "__"), x => x.Value));
        using var host = Host(provider);
        using var http = host.CreateClient();
        var tenant = "api-settings-" + Guid.NewGuid().ToString("N");

        using (var empty = await Send(http, HttpMethod.Get, "/api/v1/settings", tenant))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var json = await Json(empty);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("settings").ValueKind);
            Assert.Single(json.RootElement.GetProperty("providers").EnumerateArray());
        }
        string state;
        using (var start = await Send(http, HttpMethod.Post, "/api/v1/connectors/microsoft/authorize", tenant, new { }))
        {
            using var json = await Json(start);
            state = HttpUtility.ParseQueryString(
                new Uri(json.RootElement.GetProperty("authorizationUrl").GetString()!).Query)["state"]!;
        }
        Guid accountId;
        using (var complete = await Send(http, HttpMethod.Post, "/api/v1/connectors/complete", tenant, new { state, code = "c" }))
        using (var json = await Json(complete))
            accountId = json.RootElement.GetProperty("id").GetGuid();

        object Body(string[] delegations, string agentMode = "act_with_confirmation") => new
        {
            displayName = "Ale", workStart = "08:30", workEnd = "18:00",
            tools = new { outlook = true, teams = true, microsoftTodo = true, gmail = false, googleCalendar = false,
                googleTasks = false, crm = "hubspot" },
            accounts = new[]
            {
                new { id = Guid.NewGuid(), provider = "microsoft", usage = "work", label = "ACME",
                    connectedAccountId = (Guid?)accountId, delegations },
                new { id = Guid.NewGuid(), provider = "google", usage = "personal", label = "Gmail",
                    connectedAccountId = (Guid?)null, delegations = new[] { "mail.read" } }
            },
            agentMode, completeOnboarding = true
        };

        using (var invalid = await Send(http, HttpMethod.Put, "/api/v1/settings", tenant, Body(["chats.write"])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var json = await Json(invalid);
            Assert.Equal("accounts.delegations", json.RootElement.GetProperty("field").GetString());
        }
        using (var saved = await Send(http, HttpMethod.Put, "/api/v1/settings", tenant, Body(["mail.read", "tasks.read"])))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var json = await Json(saved);
            Assert.False(json.RootElement.GetProperty("onboardingCompletedAt").ValueKind == JsonValueKind.Null);
        }
        using (var others = await Send(http, HttpMethod.Get, "/api/v1/settings", tenant, subject: "bob"))
        using (var json = await Json(others))
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("settings").ValueKind);

        var taskKey = WorkItemKey.Format(ConnectorProviders.Microsoft, accountId, WorkItemKind.Task, "L|T1");
        using (var today = await Send(http, HttpMethod.Get, "/api/v1/today", tenant))
        using (var json = await Json(today))
            Assert.Equal(["mail", "task"], json.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("item").GetProperty("kind").GetString()).Order());
        using (var denied = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = taskKey, decision = "done", applyToSource = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var json = await Json(denied);
            Assert.Equal("delegation_required", json.RootElement.GetProperty("error").GetString());
        }
        Assert.Empty(provider.Completed);

        using (await Send(http, HttpMethod.Put, "/api/v1/settings", tenant, Body(["tasks.read"]))) { }
        using (var tasksOnly = await Send(http, HttpMethod.Get, "/api/v1/today", tenant))
        using (var json = await Json(tasksOnly))
            Assert.Equal(["task"], json.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("item").GetProperty("kind").GetString()));
        using (await Send(http, HttpMethod.Put, "/api/v1/settings", tenant, Body([]))) { }
        using (var paused = await Send(http, HttpMethod.Get, "/api/v1/today", tenant))
        using (var json = await Json(paused))
            Assert.Equal("paused", Assert.Single(json.RootElement.GetProperty("accounts").EnumerateArray())
                .GetProperty("status").GetString());

        using (await Send(http, HttpMethod.Put, "/api/v1/settings", tenant, Body(["tasks.read", "tasks.complete"]))) { }
        using (var done = await Send(http, HttpMethod.Post, "/api/v1/today/decisions", tenant,
            new { key = taskKey, decision = "done", applyToSource = true }))
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(["L|T1"], provider.Completed);

        using (var recap = await Send(http, HttpMethod.Get, "/api/v1/today/recap?days=3&timeZone=Europe/Rome", tenant))
        {
            Assert.Equal(HttpStatusCode.OK, recap.StatusCode);
            using var json = await Json(recap);
            var days = json.RootElement.GetProperty("days").EnumerateArray().ToArray();
            Assert.Equal(3, days.Length);
            Assert.Equal(1, days[^1].GetProperty("done").GetInt32());
        }
    }

    [Fact]
    public void PublicBaseUrlMustBeHttpsOrLoopback()
    {
        Assert.Null(Tao.Api.ConnectorEndpoints.RedirectUri(null, development: false));
        Assert.Equal(new Uri("https://tao.example.test/connections/callback"),
            Tao.Api.ConnectorEndpoints.RedirectUri("https://tao.example.test/", development: false));
        Assert.Equal(new Uri("http://localhost:59800/connections/callback"),
            Tao.Api.ConnectorEndpoints.RedirectUri("http://localhost:59800", development: false));
        Assert.Throws<InvalidOperationException>(() =>
            Tao.Api.ConnectorEndpoints.RedirectUri("http://tao.example.test", development: false));
        Assert.Throws<InvalidOperationException>(() =>
            Tao.Api.ConnectorEndpoints.RedirectUri("https://tao.example.test/?x=1", development: false));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string tenant,
        object? body = null, string subject = "alice")
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("X-Test-Tenant", tenant);
        request.Headers.Add("X-Test-Subject", subject);
        return await http.SendAsync(request);
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

    private static Dictionary<string, string?> Settings()
    {
        var migration = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        var runtime = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        var retention = Environment.GetEnvironmentVariable("TAO_TEST_RETENTION_DB");
        var keyRing = Environment.GetEnvironmentVariable("TAO_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(migration));
        Assert.False(string.IsNullOrWhiteSpace(runtime));
        Assert.False(string.IsNullOrWhiteSpace(retention));
        Assert.False(string.IsNullOrWhiteSpace(keyRing));
        return new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = runtime,
            ["Database:MigrationConnectionString"] = migration,
            ["Database:RetentionConnectionString"] = retention,
            ["Crypto:KeyRingPath"] = keyRing,
            ["Auth:Issuer"] = "http://localhost:8081/realms/tao",
            ["Auth:Audience"] = "tao-api",
            ["Embeddings:Provider"] = "none",
            ["Tao:PublicBaseUrl"] = "https://tao.example.test",
            ["RateLimit:RequestsPerMinute"] = "500",
            ["RateLimit:TenantRequestsPerMinute"] = "1000"
        };
    }

    private static WebApplicationFactory<Program> Host(ScriptedProvider provider, IChatModel? model = null)
    {
        var cfg = Settings();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IConnectorProvider>();
                s.AddSingleton<IConnectorProvider>(provider);
                if (model is not null)
                {
                    s.RemoveAll<IChatModel>();
                    s.AddSingleton(model);
                }
                s.AddAuthentication(opt =>
                    {
                        opt.DefaultAuthenticateScheme = SignedFixture.SchemeName;
                        opt.DefaultChallengeScheme = SignedFixture.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, SignedFixture>(SignedFixture.SchemeName, _ => { });
            });
        });
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _old = [];

        public TemporaryEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _old[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _old)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class ScriptedProvider : IConnectorProvider
    {
        public Uri? Redirect { get; private set; }
        public List<string> Completed { get; } = [];
        public int Revoked;
        private int _issued;

        public string Id => ConnectorProviders.Microsoft;
        public string DisplayName => "Microsoft 365";
        public bool IsConfigured => true;
        public IReadOnlyList<string> Capabilities => ["tasks.complete"];

        public Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri)
        {
            Redirect = redirectUri;
            return new Uri("https://login.example.test/authorize?state=" + Uri.EscapeDataString(state));
        }

        public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri,
            CancellationToken ct) => Task.FromResult(new OAuthTokens("access-" + Interlocked.Increment(ref _issued),
            "refresh", DateTimeOffset.UtcNow.AddHours(1), ["Tasks.ReadWrite"]));

        public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
            ExchangeCodeAsync("", "", Redirect!, ct);

        public Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult(new ConnectedIdentity("work-1", "Alice Work", "alice@work.example"));

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
            string accessToken, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> capabilities,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItem>>(
            [
                new WorkItem(Id, accountId, WorkItemKind.Mail, "m1", "Nota", Unread: true),
                new WorkItem(Id, accountId, WorkItemKind.Task, "L|T1", "Preventivo", DueAt: DateTimeOffset.UtcNow.AddHours(-1))
            ]);

        public Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct)
        {
            lock (Completed) Completed.Add(externalTaskId);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(OAuthTokens tokens, CancellationToken ct)
        {
            Interlocked.Increment(ref Revoked);
            return Task.CompletedTask;
        }
    }

    private sealed class SignedFixture(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ConnectorFixture";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("tenant_id", tenant), new Claim("sub", subject)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
