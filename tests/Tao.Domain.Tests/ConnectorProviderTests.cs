using System.Net;
using System.Text;
using System.Web;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure.Connectors;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ConnectorProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly Uri Redirect = new("https://tao.example.test/connections/callback");
    private static readonly Guid Account = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly ConnectedIdentity Me = new("me-id", "Alice", "alice@example.test");

    [Fact]
    public void MicrosoftAuthorizationRequestsDelegatedScopesWithPkce()
    {
        var (graph, _) = Microsoft();
        var uri = graph.BuildAuthorizationUri("state-1", "challenge-1", Redirect);
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal(("client-id", "code", "S256", "challenge-1", "state-1", Redirect.ToString()),
            (query["client_id"], query["response_type"], query["code_challenge_method"], query["code_challenge"],
             query["state"], query["redirect_uri"]));
        Assert.Contains("offline_access", query["scope"]!.Split(' '));
        Assert.Contains("Tasks.ReadWrite", query["scope"]!.Split(' '));
        Assert.DoesNotContain("Mail.ReadWrite", query["scope"]!.Split(' '));
    }

    [Fact]
    public async Task MicrosoftCodeExchangeSendsVerifierAndSecretAndParsesTokens()
    {
        var (graph, http) = Microsoft();
        http.Respond = _ => (HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":3599,"scope":"User.Read Mail.Read"}""");

        var tokens = await graph.ExchangeCodeAsync("code-1", "verifier-1", Redirect, default);

        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/organizations/oauth2/v2.0/token", request.Uri.AbsolutePath);
        var form = HttpUtility.ParseQueryString(request.Body!);
        Assert.Equal(("authorization_code", "code-1", "verifier-1", "secret", Redirect.ToString()),
            (form["grant_type"], form["code"], form["code_verifier"], form["client_secret"], form["redirect_uri"]));
        Assert.Equal(("at", "rt", Now.AddSeconds(3599)), (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt));
        Assert.Equal(["User.Read", "Mail.Read"], tokens.Scopes);
    }

    [Fact]
    public async Task RejectedRefreshMeansReconnectWithoutLeakingTheProviderBody()
    {
        var (graph, http) = Microsoft();
        http.Respond = _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"secret detail"}""");
        var error = await Assert.ThrowsAsync<ConnectorUnavailableException>(() =>
            graph.RefreshAsync("rt", default));
        Assert.Equal("reconnect_required", error.Code);
        Assert.DoesNotContain("secret detail", error.ToString());
    }

    [Fact]
    public async Task MicrosoftWorkItemsKeepOnlyActionableSignals()
    {
        var (graph, http) = Microsoft();
        http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me/calendarView" => (HttpStatusCode.OK, """
                {"value":[
                  {"id":"e1","subject":"Standup","start":{"dateTime":"2026-11-25T09:00:00.0000000","timeZone":"UTC"},
                   "end":{"dateTime":"2026-11-25T09:15:00.0000000","timeZone":"UTC"},"isCancelled":false,
                   "organizer":{"emailAddress":{"name":"Bob"}},"onlineMeeting":{"joinUrl":"https://teams.example/join"},
                   "webLink":"https://outlook.example/e1"},
                  {"id":"e2","subject":"Cancelled","isCancelled":true,"start":{"dateTime":"2026-11-25T10:00:00"}},
                  {"id":"e3","subject":"Declined","responseStatus":{"response":"declined"},"start":{"dateTime":"2026-11-25T11:00:00"}}
                ]}
                """),
            "/v1.0/me/mailFolders/inbox/messages" => (HttpStatusCode.OK, """
                {"value":[
                  {"id":"m1","subject":"Contratto","isRead":false,"importance":"normal","bodyPreview":"Ciao,   ecco",
                   "from":{"emailAddress":{"name":"Carla","address":"carla@example.test"}},"receivedDateTime":"2026-11-25T07:30:00Z"},
                  {"id":"m2","subject":"Newsletter","isRead":true,"importance":"normal"},
                  {"id":"m3","subject":"Urgente","isRead":true,"importance":"normal","flag":{"flagStatus":"flagged"}}
                ]}
                """),
            "/v1.0/me/todo/lists" => (HttpStatusCode.OK, """{"value":[{"id":"L1","displayName":"Lavoro"}]}"""),
            "/v1.0/me/todo/lists/L1/tasks" => (HttpStatusCode.OK, """
                {"value":[
                  {"id":"T1","title":"Offerta","importance":"high","dueDateTime":{"dateTime":"2026-11-25T00:00:00.0000000","timeZone":"UTC"}},
                  {"id":"T2","title":"Futuro","dueDateTime":{"dateTime":"2026-12-25T00:00:00.0000000","timeZone":"UTC"}},
                  {"id":"T3","title":"Senza scadenza"}
                ]}
                """),
            "/v1.0/me/chats" => (HttpStatusCode.OK, """
                {"value":[
                  {"id":"c1","topic":null,"webUrl":"https://teams.example/c1","lastMessagePreview":{"id":"p1",
                   "createdDateTime":"2026-11-25T07:50:00Z","body":{"content":"<p>Mi chiami &amp; poi?</p>"},
                   "from":{"user":{"id":"bob-id","displayName":"Bob"}}}},
                  {"id":"c2","lastMessagePreview":{"id":"p2","createdDateTime":"2026-11-25T07:55:00Z",
                   "from":{"user":{"id":"me-id","displayName":"Alice"}}}},
                  {"id":"c3","viewpoint":{"lastMessageReadDateTime":"2026-11-25T07:59:00Z"},"lastMessagePreview":{"id":"p3",
                   "createdDateTime":"2026-11-25T07:58:00Z","from":{"user":{"id":"bob-id","displayName":"Bob"}}}}
                ]}
                """),
            _ => (HttpStatusCode.NotFound, "{}")
        };

        var items = await graph.GetWorkItemsAsync(Account, Me, "access", Now.AddHours(-8), Now.AddHours(16), default);

        Assert.Equal(["e1", "m1", "m3", "L1|T1", "L1|T3", "c1|p1"], items.Select(i => i.ExternalId));
        var standup = items[0];
        Assert.Equal((WorkItemKind.Event, "Standup", "Bob", "https://teams.example/join"),
            (standup.Kind, standup.Title, standup.Counterpart, standup.WebLink));
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), standup.StartsAt);
        Assert.Equal(("Ciao, ecco", "Carla", true), (items[1].Detail, items[1].Counterpart, items[1].Unread));
        Assert.True(items[2].Important);
        Assert.Equal(("Lavoro", true), (items[3].Detail, items[3].Important));
        Assert.Equal(("Bob", "Mi chiami & poi?"), (items[5].Title, items[5].Detail));
        Assert.All(http.Requests, r => Assert.Equal("Bearer access", r.Auth));
        Assert.Equal("outlook.timezone=\"UTC\"", http.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("calendarView")).Prefer);
        Assert.All(http.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task MissingOptionalPermissionHidesOnlyThatCategoryButExpiredTokenFailsTheAccount()
    {
        var (graph, http) = Microsoft();
        http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me/calendarView" => (HttpStatusCode.OK, """{"value":[{"id":"e1","subject":"Demo"}]}"""),
            "/v1.0/me/chats" => (HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden"}}"""),
            _ => (HttpStatusCode.OK, """{"value":[]}""")
        };
        Assert.Equal(["e1"], (await graph.GetWorkItemsAsync(Account, Me, "access", Now, Now.AddHours(8), default))
            .Select(i => i.ExternalId));

        http.Respond = _ => (HttpStatusCode.Unauthorized, "{}");
        var error = await Assert.ThrowsAsync<ConnectorUnavailableException>(() =>
            graph.GetWorkItemsAsync(Account, Me, "access", Now, Now.AddHours(8), default));
        Assert.Equal("reconnect_required", error.Code);
    }

    [Fact]
    public async Task CompletingAMicrosoftTaskPatchesOnlyItsStatus()
    {
        var (graph, http) = Microsoft();
        http.Respond = _ => (HttpStatusCode.OK, "{}");
        await graph.CompleteTaskAsync("access", "L 1|T/1", default);

        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/todo/lists/L%201/tasks/T%2F1", request.Uri.AbsoluteUri);
        Assert.Equal("""{"status":"completed"}""", request.Body);
        await Assert.ThrowsAsync<ArgumentException>(() => graph.CompleteTaskAsync("access", "no-list", default));
    }

    [Fact]
    public void GoogleAuthorizationAsksForOfflineAccessWithPkce()
    {
        var (google, _) = Google();
        var query = HttpUtility.ParseQueryString(google.BuildAuthorizationUri("s", "c", Redirect).Query);
        Assert.Equal(("offline", "consent", "S256", "c"),
            (query["access_type"], query["prompt"], query["code_challenge_method"], query["code_challenge"]));
        Assert.Contains("https://www.googleapis.com/auth/gmail.readonly", query["scope"]!.Split(' '));
    }

    [Fact]
    public async Task GoogleWorkItemsMapCalendarGmailAndTasks()
    {
        var (google, http) = Google();
        http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/calendar/v3/calendars/primary/events" => (HttpStatusCode.OK, """
                {"items":[
                  {"id":"g1","status":"confirmed","summary":"Review","start":{"dateTime":"2026-11-25T10:00:00+01:00"},
                   "end":{"dateTime":"2026-11-25T11:00:00+01:00"},"organizer":{"email":"boss@example.test"},"htmlLink":"https://cal/g1"},
                  {"id":"g2","status":"cancelled","summary":"Annullato"},
                  {"id":"g3","summary":"Rifiutato","attendees":[{"self":true,"responseStatus":"declined"}]}
                ]}
                """),
            "/gmail/v1/users/me/messages" => (HttpStatusCode.OK, """{"messages":[{"id":"x1"}]}"""),
            "/gmail/v1/users/me/messages/x1" => (HttpStatusCode.OK, """
                {"id":"x1","threadId":"t1","labelIds":["INBOX","UNREAD","IMPORTANT"],"snippet":"Ci vediamo l&#39;8?",
                 "internalDate":"1795593600000","payload":{"headers":[{"name":"Subject","value":"Riunione"},
                 {"name":"From","value":"Mario Rossi <mario@example.test>"}]}}
                """),
            "/tasks/v1/users/@me/lists" => (HttpStatusCode.OK, """{"items":[{"id":"GL","title":"Personale"}]}"""),
            "/tasks/v1/lists/GL/tasks" => (HttpStatusCode.OK, """
                {"items":[{"id":"GT","title":"Fattura","status":"needsAction","due":"2026-11-25T00:00:00.000Z"},
                          {"id":"GX","title":"Fatto","status":"completed"}]}
                """),
            _ => (HttpStatusCode.NotFound, "{}")
        };

        var items = await google.GetWorkItemsAsync(Account, Me, "access", Now, Now.AddHours(16), default);

        Assert.Equal(["g1", "x1", "GL|GT"], items.Select(i => i.ExternalId));
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), items[0].StartsAt);
        Assert.Equal(("Riunione", "Mario Rossi", "Ci vediamo l'8?", true, true),
            (items[1].Title, items[1].Counterpart, items[1].Detail, items[1].Unread, items[1].Important));
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 23, 59, 59, TimeSpan.Zero), items[2].DueAt);
        var search = HttpUtility.ParseQueryString(
            http.Requests.Single(r => r.Uri.AbsolutePath == "/gmail/v1/users/me/messages").Uri.Query);
        Assert.Equal($"in:inbox after:{Now.ToUnixTimeSeconds()} (is:unread OR is:important OR is:starred)", search["q"]);
    }

    [Fact]
    public async Task GoogleDisconnectRevokesTheRefreshToken()
    {
        var (google, http) = Google();
        http.Respond = _ => (HttpStatusCode.OK, "{}");
        await google.RevokeAsync(new OAuthTokens("at", "rt", Now, []), default);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://oauth2.googleapis.com/revoke", request.Uri.ToString());
        Assert.Equal("token=rt", request.Body);
    }

    private static (MicrosoftGraphConnector, FakeHttp) Microsoft()
    {
        var http = new FakeHttp();
        return (new MicrosoftGraphConnector(new HttpClient(http),
            new MicrosoftConnectorOptions { ClientId = "client-id", ClientSecret = "secret" }, new FixedClock()), http);
    }

    private static (GoogleWorkspaceConnector, FakeHttp) Google()
    {
        var http = new FakeHttp();
        return (new GoogleWorkspaceConnector(new HttpClient(http),
            new GoogleConnectorOptions { ClientId = "g-client", ClientSecret = "g-secret" }, new FixedClock()), http);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? Body, string? Prefer)> Requests { get; } = [];
        public Func<HttpRequestMessage, (HttpStatusCode, string)> Respond { get; set; } =
            _ => (HttpStatusCode.NotFound, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body,
                    request.Headers.TryGetValues("Prefer", out var prefer) ? string.Join(",", prefer) : null));
            var (status, json) = Respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
