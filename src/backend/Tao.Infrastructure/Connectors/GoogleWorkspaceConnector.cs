using System.Net.Mail;
using Tao.Application;
using Tao.Domain;
using static Tao.Infrastructure.Connectors.ConnectorHttp;

namespace Tao.Infrastructure.Connectors;

public sealed class GoogleConnectorOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>
/// Google Workspace (OAuth 2.0 web server flow + PKCE, offline access). Reads Calendar, Gmail
/// metadata/snippets and Tasks; the only write is completing a task after explicit confirmation.
/// </summary>
public sealed class GoogleWorkspaceConnector(HttpClient http, GoogleConnectorOptions options, TimeProvider time)
    : IConnectorProvider
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public static readonly IReadOnlyList<string> Scopes =
    [
        "openid", "email", "profile",
        "https://www.googleapis.com/auth/calendar.readonly",
        "https://www.googleapis.com/auth/gmail.readonly",
        "https://www.googleapis.com/auth/tasks"
    ];
    private const int MaxMessages = 20;

    public string Id => ConnectorProviders.Google;
    public string DisplayName => "Google Workspace";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.ClientSecret);
    public IReadOnlyList<string> Capabilities => ConnectorCapabilities.Google;

    public Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri) =>
        new("https://accounts.google.com/o/oauth2/v2/auth?" + Query(
            ("client_id", options.ClientId!), ("response_type", "code"),
            ("redirect_uri", redirectUri.ToString()), ("scope", string.Join(' ', Scopes)),
            ("access_type", "offline"), ("prompt", "consent"), ("include_granted_scopes", "true"),
            ("state", state), ("code_challenge", codeChallenge), ("code_challenge_method", "S256")));

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri,
        CancellationToken ct) =>
        RequestTokensAsync(http, TokenEndpoint,
        [
            new("client_id", options.ClientId!), new("client_secret", options.ClientSecret!),
            new("grant_type", "authorization_code"), new("code", code),
            new("redirect_uri", redirectUri.ToString()), new("code_verifier", codeVerifier)
        ], time, Scopes, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokensAsync(http, TokenEndpoint,
        [
            new("client_id", options.ClientId!), new("client_secret", options.ClientSecret!),
            new("grant_type", "refresh_token"), new("refresh_token", refreshToken)
        ], time, Scopes, ct);

    public async Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct)
    {
        using var info = await GetJsonAsync(http, "https://openidconnect.googleapis.com/v1/userinfo",
            accessToken, ct);
        var root = info.RootElement;
        var id = String(root, "sub") ?? throw new ConnectorUnavailableException("invalid_identity");
        var email = String(root, "email");
        return new ConnectedIdentity(id, String(root, "name") ?? email ?? "Google", email);
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
        string accessToken, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> capabilities,
        CancellationToken ct)
    {
        Task<IReadOnlyList<WorkItem>> When(string capability, Func<Task<IReadOnlyList<WorkItem>>> load) =>
            capabilities.Contains(capability) ? load() : Task.FromResult<IReadOnlyList<WorkItem>>([]);
        var events = When(ConnectorCapabilities.CalendarRead, () => EventsAsync(accountId, accessToken, from, to, ct));
        var mail = When(ConnectorCapabilities.MailRead,
            () => OptionalAsync(() => MailAsync(accountId, identity, accessToken, from, ct)));
        var tasks = When(ConnectorCapabilities.TasksRead, () => OptionalAsync(() => TasksAsync(accountId, accessToken, to, ct)));
        await Task.WhenAll(events, mail, tasks);
        return [.. events.Result, .. mail.Result, .. tasks.Result];
    }

    public Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct)
    {
        var parts = externalTaskId.Split('|');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("invalid_task");
        return PatchJsonAsync(http,
            $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(parts[0])}/tasks/{Uri.EscapeDataString(parts[1])}",
            accessToken, """{"status":"completed"}""", ct);
    }

    public async Task RevokeAsync(OAuthTokens tokens, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("token", tokens.RefreshToken ?? tokens.AccessToken)]);
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", content, ct);
        EnsureSuccess(response);
    }

    private async Task<IReadOnlyList<WorkItem>> EventsAsync(Guid accountId, string token, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        using var json = await GetJsonAsync(http, "https://www.googleapis.com/calendar/v3/calendars/primary/events?" +
            Query(("timeMin", from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                ("timeMax", to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                ("singleEvents", "true"), ("orderBy", "startTime"), ("maxResults", "50")), token, ct);
        return Items(json, "items")
            .Where(e => String(e, "status") != "cancelled" &&
                !(Child(e, "attendees").ValueKind == System.Text.Json.JsonValueKind.Array &&
                  Child(e, "attendees").EnumerateArray().Any(a =>
                      Bool(a, "self") && String(a, "responseStatus") == "declined")))
            .Select(e => new WorkItem(Id, accountId, WorkItemKind.Event, String(e, "id") ?? "",
                Clip(String(e, "summary") ?? "(senza titolo)", MaxTitleLength),
                Detail: String(e, "location") is { } location ? Clip(location, MaxDetailLength) : null,
                Counterpart: String(Child(e, "organizer"), "displayName") ?? String(Child(e, "organizer"), "email"),
                StartsAt: Utc(String(Child(e, "start"), "dateTime") ?? String(Child(e, "start"), "date")),
                EndsAt: Utc(String(Child(e, "end"), "dateTime") ?? String(Child(e, "end"), "date")),
                WebLink: String(e, "hangoutLink") ?? String(e, "htmlLink")))
            .Where(i => i.ExternalId.Length > 0)
            .ToArray();
    }

    private async Task<IReadOnlyList<WorkItem>> MailAsync(Guid accountId, ConnectedIdentity identity,
        string token, DateTimeOffset from, CancellationToken ct)
    {
        using var list = await GetJsonAsync(http, "https://gmail.googleapis.com/gmail/v1/users/me/messages?" +
            Query(("q", $"in:inbox after:{from.ToUnixTimeSeconds()} (is:unread OR is:important OR is:starred)"),
                ("maxResults", MaxMessages.ToString())), token, ct);
        var ids = Items(list, "messages").Select(m => String(m, "id")).OfType<string>().Take(MaxMessages).ToArray();
        using var gate = new SemaphoreSlim(5);
        var messages = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var message = await GetJsonAsync(http,
                    $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(id)}?" + Query(
                        ("format", "metadata"), ("metadataHeaders", "Subject"), ("metadataHeaders", "From")),
                    token, ct);
                var root = message.RootElement;
                var labels = Child(root, "labelIds").ValueKind == System.Text.Json.JsonValueKind.Array
                    ? Child(root, "labelIds").EnumerateArray().Select(l => l.GetString()).ToHashSet()
                    : new HashSet<string?>();
                var headers = Child(Child(root, "payload"), "headers");
                string? Header(string name) => headers.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? headers.EnumerateArray().Where(h => string.Equals(String(h, "name"), name,
                        StringComparison.OrdinalIgnoreCase)).Select(h => String(h, "value")).FirstOrDefault()
                    : null;
                var thread = String(root, "threadId") ?? id;
                return new WorkItem(Id, accountId, WorkItemKind.Mail, id,
                    Clip(Header("Subject") ?? "(nessun oggetto)", MaxTitleLength),
                    Detail: PlainText(String(root, "snippet"), MaxDetailLength),
                    Counterpart: SenderName(Header("From")),
                    StartsAt: long.TryParse(String(root, "internalDate"), out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                    Important: labels.Contains("IMPORTANT") || labels.Contains("STARRED"),
                    Unread: labels.Contains("UNREAD"),
                    WebLink: "https://mail.google.com/mail/?" + Query(("authuser", identity.Email ?? "0")) +
                        "#all/" + Uri.EscapeDataString(thread));
            }
            finally { gate.Release(); }
        }));
        return messages;
    }

    private async Task<IReadOnlyList<WorkItem>> TasksAsync(Guid accountId, string token, DateTimeOffset to,
        CancellationToken ct)
    {
        using var lists = await GetJsonAsync(http,
            "https://tasks.googleapis.com/tasks/v1/users/@me/lists?maxResults=20", token, ct);
        var listIds = Items(lists, "items")
            .Select(l => (Id: String(l, "id"), Name: String(l, "title")))
            .Where(l => !string.IsNullOrEmpty(l.Id)).Take(10).ToArray();
        var perList = await Task.WhenAll(listIds.Select(async list =>
        {
            using var tasks = await GetJsonAsync(http,
                $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(list.Id!)}/tasks?" + Query(
                    ("showCompleted", "false"), ("showHidden", "false"), ("maxResults", "50")), token, ct);
            return Items(tasks, "items")
                .Where(t => String(t, "status") != "completed" && String(t, "id") is { Length: > 0 })
                .Select(t => new WorkItem(Id, accountId, WorkItemKind.Task, $"{list.Id}|{String(t, "id")}",
                    Clip(String(t, "title") is { Length: > 0 } title ? title : "(senza titolo)", MaxTitleLength),
                    Detail: list.Name,
                    // Google Tasks keeps only the date: the task is due by the end of that day.
                    DueAt: Utc(String(t, "due"))?.Date is { } day
                        ? new DateTimeOffset(day, TimeSpan.Zero).AddDays(1).AddSeconds(-1) : null,
                    WebLink: String(t, "webViewLink")))
                .Where(t => t.DueAt is null || t.DueAt <= to.AddDays(1))
                .ToArray();
        }));
        return perList.SelectMany(x => x).ToArray();
    }

    private static string? SenderName(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        try
        {
            var address = new MailAddress(from);
            return string.IsNullOrWhiteSpace(address.DisplayName) ? address.Address : address.DisplayName;
        }
        catch (FormatException) { return Clip(from, 80); }
    }
}
