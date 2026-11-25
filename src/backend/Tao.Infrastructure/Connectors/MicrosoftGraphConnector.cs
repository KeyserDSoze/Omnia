using System.Text.Json;
using Tao.Application;
using Tao.Domain;
using static Tao.Infrastructure.Connectors.ConnectorHttp;

namespace Tao.Infrastructure.Connectors;

public sealed class MicrosoftConnectorOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>"organizations" (work/school accounts of any tenant), "common" or a tenant id.</summary>
    public string Tenant { get; set; } = "organizations";
}

/// <summary>
/// Microsoft 365 through Microsoft Graph (delegated permissions, authorization code + PKCE,
/// confidential client). Reads calendar, inbox, To Do and Teams chats; the only write is
/// marking a To Do task completed after the user confirmed it in Tao.
/// </summary>
public sealed class MicrosoftGraphConnector(HttpClient http, MicrosoftConnectorOptions options, TimeProvider time)
    : IConnectorProvider
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";
    public static readonly IReadOnlyList<string> Scopes =
    [
        "openid", "profile", "email", "offline_access", "User.Read", "Mail.Read", "Calendars.Read",
        "Tasks.ReadWrite", "Chat.Read"
    ];

    public string Id => ConnectorProviders.Microsoft;
    public string DisplayName => "Microsoft 365";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.ClientSecret) && !string.IsNullOrWhiteSpace(options.Tenant);
    public IReadOnlyList<string> Capabilities => ConnectorCapabilities.Microsoft;

    private string Authority =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.Tenant)}/oauth2/v2.0";

    public Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri) =>
        new($"{Authority}/authorize?" + Query(
            ("client_id", options.ClientId!), ("response_type", "code"),
            ("redirect_uri", redirectUri.ToString()), ("response_mode", "query"),
            ("scope", string.Join(' ', Scopes)), ("state", state),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"),
            ("prompt", "select_account")));

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri,
        CancellationToken ct) =>
        RequestTokensAsync(http, $"{Authority}/token",
        [
            new("client_id", options.ClientId!), new("client_secret", options.ClientSecret!),
            new("grant_type", "authorization_code"), new("code", code),
            new("redirect_uri", redirectUri.ToString()), new("code_verifier", codeVerifier),
            new("scope", string.Join(' ', Scopes))
        ], time, Scopes, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokensAsync(http, $"{Authority}/token",
        [
            new("client_id", options.ClientId!), new("client_secret", options.ClientSecret!),
            new("grant_type", "refresh_token"), new("refresh_token", refreshToken),
            new("scope", string.Join(' ', Scopes))
        ], time, Scopes, ct);

    public async Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct)
    {
        using var me = await GetJsonAsync(http, $"{GraphBase}/me?$select=id,displayName,mail,userPrincipalName",
            accessToken, ct);
        var root = me.RootElement;
        var id = String(root, "id") ?? throw new ConnectorUnavailableException("invalid_identity");
        var email = String(root, "mail") ?? String(root, "userPrincipalName");
        return new ConnectedIdentity(id, String(root, "displayName") ?? email ?? "Microsoft 365", email);
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
        string accessToken, DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string> capabilities,
        CancellationToken ct)
    {
        Task<IReadOnlyList<WorkItem>> When(string capability, Func<Task<IReadOnlyList<WorkItem>>> load) =>
            capabilities.Contains(capability) ? load() : Task.FromResult<IReadOnlyList<WorkItem>>([]);
        var events = When(ConnectorCapabilities.CalendarRead, () => EventsAsync(accountId, accessToken, from, to, ct));
        var mail = When(ConnectorCapabilities.MailRead, () => OptionalAsync(() => MailAsync(accountId, accessToken, from, ct)));
        var tasks = When(ConnectorCapabilities.TasksRead, () => OptionalAsync(() => TasksAsync(accountId, accessToken, to, ct)));
        var chats = When(ConnectorCapabilities.ChatsRead,
            () => OptionalAsync(() => ChatsAsync(accountId, identity, accessToken, from, ct)));
        await Task.WhenAll(events, mail, tasks, chats);
        return [.. events.Result, .. mail.Result, .. tasks.Result, .. chats.Result];
    }

    public Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct)
    {
        var parts = externalTaskId.Split('|');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("invalid_task");
        return PatchJsonAsync(http,
            $"{GraphBase}/me/todo/lists/{Uri.EscapeDataString(parts[0])}/tasks/{Uri.EscapeDataString(parts[1])}",
            accessToken, """{"status":"completed"}""", ct);
    }

    // Microsoft has no per-app token revocation for delegated grants: the user removes the app
    // consent from https://myapps.microsoft.com. Tao deletes its sealed tokens.
    public Task RevokeAsync(OAuthTokens tokens, CancellationToken ct) => Task.CompletedTask;

    private async Task<IReadOnlyList<WorkItem>> EventsAsync(Guid accountId, string token, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        using var json = await GetJsonAsync(http, $"{GraphBase}/me/calendarView?" + Query(
            ("startDateTime", from.UtcDateTime.ToString("O")), ("endDateTime", to.UtcDateTime.ToString("O")),
            ("$select", "id,subject,start,end,isCancelled,webLink,organizer,location,responseStatus,onlineMeeting"),
            ("$orderby", "start/dateTime"), ("$top", "50")), token, ct, "outlook.timezone=\"UTC\"");
        return Items(json, "value")
            .Where(e => !Bool(e, "isCancelled") &&
                String(Child(e, "responseStatus"), "response") != "declined")
            .Select(e => new WorkItem(Id, accountId, WorkItemKind.Event, String(e, "id") ?? "",
                Clip(String(e, "subject") ?? "(senza titolo)", MaxTitleLength),
                Detail: NullIfEmpty(Clip(String(Child(e, "location"), "displayName"), MaxDetailLength)),
                Counterpart: String(Child(Child(e, "organizer"), "emailAddress"), "name"),
                StartsAt: Utc(String(Child(e, "start"), "dateTime")),
                EndsAt: Utc(String(Child(e, "end"), "dateTime")),
                WebLink: String(Child(e, "onlineMeeting"), "joinUrl") ?? String(e, "webLink")))
            .Where(i => i.ExternalId.Length > 0)
            .ToArray();
    }

    private async Task<IReadOnlyList<WorkItem>> MailAsync(Guid accountId, string token, DateTimeOffset from,
        CancellationToken ct)
    {
        using var json = await GetJsonAsync(http, $"{GraphBase}/me/mailFolders/inbox/messages?" + Query(
            ("$filter", $"receivedDateTime ge {from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"),
            ("$orderby", "receivedDateTime desc"),
            ("$select", "id,subject,from,receivedDateTime,isRead,importance,bodyPreview,webLink,flag"),
            ("$top", "25")), token, ct);
        return Items(json, "value")
            .Select(m => new WorkItem(Id, accountId, WorkItemKind.Mail, String(m, "id") ?? "",
                Clip(String(m, "subject") ?? "(nessun oggetto)", MaxTitleLength),
                Detail: NullIfEmpty(Clip(String(m, "bodyPreview"), MaxDetailLength)),
                Counterpart: Sender(Child(Child(m, "from"), "emailAddress")),
                StartsAt: Utc(String(m, "receivedDateTime")),
                Important: String(m, "importance") == "high" ||
                    String(Child(m, "flag"), "flagStatus") == "flagged",
                Unread: !Bool(m, "isRead"),
                WebLink: String(m, "webLink")))
            .Where(i => i.ExternalId.Length > 0 && (i.Unread || i.Important))
            .ToArray();
    }

    private async Task<IReadOnlyList<WorkItem>> TasksAsync(Guid accountId, string token, DateTimeOffset to,
        CancellationToken ct)
    {
        using var lists = await GetJsonAsync(http, $"{GraphBase}/me/todo/lists?$top=20", token, ct);
        var listIds = Items(lists, "value")
            .Select(l => (Id: String(l, "id"), Name: String(l, "displayName")))
            .Where(l => !string.IsNullOrEmpty(l.Id)).Take(10).ToArray();
        var perList = await Task.WhenAll(listIds.Select(async list =>
        {
            using var tasks = await GetJsonAsync(http,
                $"{GraphBase}/me/todo/lists/{Uri.EscapeDataString(list.Id!)}/tasks?" + Query(
                    ("$filter", "status ne 'completed'"), ("$top", "50")), token, ct);
            return Items(tasks, "value")
                .Select(t => new WorkItem(Id, accountId, WorkItemKind.Task, $"{list.Id}|{String(t, "id")}",
                    Clip(String(t, "title") ?? "(senza titolo)", MaxTitleLength),
                    Detail: list.Name,
                    DueAt: Utc(String(Child(t, "dueDateTime"), "dateTime")),
                    Important: String(t, "importance") == "high",
                    WebLink: String(t, "id") is { } taskId
                        ? $"https://to-do.office.com/tasks/id/{Uri.EscapeDataString(taskId)}/details" : null))
                .Where(t => !t.ExternalId.EndsWith('|') && (t.DueAt is null || t.DueAt <= to))
                .ToArray();
        }));
        return perList.SelectMany(x => x).ToArray();
    }

    private async Task<IReadOnlyList<WorkItem>> ChatsAsync(Guid accountId, ConnectedIdentity identity,
        string token, DateTimeOffset from, CancellationToken ct)
    {
        using var json = await GetJsonAsync(http, $"{GraphBase}/me/chats?" + Query(
            ("$expand", "lastMessagePreview"), ("$top", "20"),
            ("$orderby", "lastMessagePreview/createdDateTime desc")), token, ct);
        var items = new List<WorkItem>();
        foreach (var chat in Items(json, "value"))
        {
            var preview = Child(chat, "lastMessagePreview");
            var sentAt = Utc(String(preview, "createdDateTime"));
            var sender = Child(Child(preview, "from"), "user");
            var readAt = Utc(String(Child(chat, "viewpoint"), "lastMessageReadDateTime"));
            if (sentAt is null || sentAt < from || Bool(preview, "isDeleted") ||
                String(sender, "id") == identity.ExternalId || (readAt is not null && readAt >= sentAt) ||
                String(chat, "id") is not { Length: > 0 } chatId || String(preview, "id") is not { Length: > 0 } messageId)
                continue;
            var who = String(sender, "displayName");
            items.Add(new WorkItem(Id, accountId, WorkItemKind.Chat, $"{chatId}|{messageId}",
                Clip(String(chat, "topic") ?? who ?? "Chat di Teams", MaxTitleLength),
                Detail: PlainText(String(Child(preview, "body"), "content"), MaxDetailLength),
                Counterpart: who, StartsAt: sentAt, Unread: true, WebLink: String(chat, "webUrl")));
        }
        return items;
    }

    private static string? Sender(JsonElement address) =>
        String(address, "name") is { Length: > 0 } name ? name : String(address, "address");

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
