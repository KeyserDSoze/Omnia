namespace Tao.Domain;

public static class ConnectorProviders
{
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public static readonly IReadOnlyList<string> All = [Microsoft, Google];
    public static bool IsKnown(string? id) => id is Microsoft or Google;
}

public enum WorkItemKind { Event, Mail, Task, Chat }

/// <summary>
/// A read-only signal from a connected work account (calendar, mail, task, chat).
/// Fetched live from the provider; only its key and the user's triage decision are stored.
/// </summary>
public sealed record WorkItem(
    string Provider, Guid AccountId, WorkItemKind Kind, string ExternalId, string Title,
    string? Detail = null, string? Counterpart = null, DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null, DateTimeOffset? DueAt = null, bool Important = false,
    bool Unread = false, string? WebLink = null)
{
    public string Key => WorkItemKey.Format(Provider, AccountId, Kind, ExternalId);
}

public static class WorkItemKey
{
    public const int MaxLength = 512;

    public static string Format(string provider, Guid accountId, WorkItemKind kind, string externalId) =>
        $"{provider}:{accountId:N}:{kind.ToString().ToLowerInvariant()}:{externalId}";

    public static bool TryParse(string? key, out string provider, out Guid accountId,
        out WorkItemKind kind, out string externalId)
    {
        provider = externalId = "";
        accountId = Guid.Empty;
        kind = default;
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength) return false;
        var parts = key.Split(':', 4);
        if (parts.Length != 4 || !ConnectorProviders.IsKnown(parts[0]) ||
            !Guid.TryParseExact(parts[1], "N", out accountId) ||
            !TryParseKind(parts[2], out kind) ||
            string.IsNullOrWhiteSpace(parts[3]))
            return false;
        provider = parts[0];
        externalId = parts[3];
        return true;
    }

    private static bool TryParseKind(string value, out WorkItemKind kind)
    {
        kind = value switch
        {
            "event" => WorkItemKind.Event,
            "mail" => WorkItemKind.Mail,
            "task" => WorkItemKind.Task,
            "chat" => WorkItemKind.Chat,
            _ => (WorkItemKind)(-1)
        };
        return Enum.IsDefined(kind);
    }
}

/// <summary>Deterministic, explainable ordering for the daily deck (no AI involved).</summary>
public static class WorkItemPriority
{
    public static int Score(WorkItem item, DateTimeOffset now) => item.Kind switch
    {
        WorkItemKind.Task when item.DueAt is { } due && due < now => 100,
        WorkItemKind.Task when item.DueAt is { } due && due <= now.AddHours(24) => 90,
        WorkItemKind.Task => item.Important ? 65 : 60,
        WorkItemKind.Event when item.EndsAt is { } end && end <= now => 10,
        WorkItemKind.Event when item.StartsAt is { } start && start <= now => 88,
        WorkItemKind.Event when item.StartsAt is { } start && start <= now.AddHours(2) => 85,
        WorkItemKind.Event => 50,
        WorkItemKind.Mail when item.Important => 80,
        WorkItemKind.Mail when item.Unread => 70,
        WorkItemKind.Mail => 40,
        WorkItemKind.Chat => 75,
        _ => 0
    };
}

public enum TriageDecision { Done, NotDone, Later, Dismissed }

public static class TriageDecisions
{
    public static bool TryParse(string? value, out TriageDecision decision)
    {
        (var ok, decision) = value switch
        {
            "done" => (true, TriageDecision.Done),
            "not_done" => (true, TriageDecision.NotDone),
            "later" => (true, TriageDecision.Later),
            "dismissed" => (true, TriageDecision.Dismissed),
            _ => (false, default)
        };
        return ok;
    }

    public static string ToWire(TriageDecision decision) => decision switch
    {
        TriageDecision.Done => "done",
        TriageDecision.NotDone => "not_done",
        TriageDecision.Later => "later",
        TriageDecision.Dismissed => "dismissed",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };
}
