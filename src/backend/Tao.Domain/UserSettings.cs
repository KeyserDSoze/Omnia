using System.Text.RegularExpressions;

namespace Tao.Domain;

/// <summary>What the agent may do with a connected account on the user's behalf.</summary>
public static class ConnectorCapabilities
{
    public const string CalendarRead = "calendar.read";
    public const string MailRead = "mail.read";
    public const string TasksRead = "tasks.read";
    public const string TasksComplete = "tasks.complete";
    public const string ChatsRead = "chats.read";

    public static readonly IReadOnlyList<string> Microsoft = [CalendarRead, MailRead, TasksRead, TasksComplete, ChatsRead];
    public static readonly IReadOnlyList<string> Google = [CalendarRead, MailRead, TasksRead, TasksComplete];

    public static IReadOnlyList<string> Supported(string provider) => provider switch
    {
        ConnectorProviders.Microsoft => Microsoft,
        ConnectorProviders.Google => Google,
        _ => []
    };

    public static string ForKind(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Event => CalendarRead,
        WorkItemKind.Mail => MailRead,
        WorkItemKind.Task => TasksRead,
        WorkItemKind.Chat => ChatsRead,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsRead(string capability) => capability.EndsWith(".read", StringComparison.Ordinal);
}

public enum AccountUsage { Work, Personal }

public enum AgentMode { ReadOnly, ActWithConfirmation }

/// <summary>An account the user told us about; linked to a connector account once connected.</summary>
public sealed record DeclaredAccount(Guid Id, string Provider, AccountUsage Usage, string Label,
    Guid? ConnectedAccountId, IReadOnlyList<string> Delegations);

/// <summary>Answers of the guided setup about the tools the user works with.</summary>
public sealed record WorkTools(bool Outlook, bool Teams, bool MicrosoftTodo, bool Gmail, bool GoogleCalendar,
    bool GoogleTasks, string? Crm);

public sealed class SettingsValidationException(string field) : Exception("invalid_settings")
{
    public string Field { get; } = field;
}

/// <summary>
/// Settings composed by the scripted setup (no AI). They tell the agent which accounts exist,
/// which are connected and what it may do on behalf of the user for each of them.
/// </summary>
public sealed partial record UserSettings(string? DisplayName, string? WorkStart, string? WorkEnd, WorkTools Tools,
    IReadOnlyList<DeclaredAccount> Accounts, AgentMode AgentMode, DateTimeOffset? OnboardingCompletedAt = null)
{
    public const int MaxAccounts = 10;
    public static readonly IReadOnlyList<string> CrmSystems = ["dynamics365", "salesforce", "hubspot", "other"];

    /// <summary>Settings before the user ran the setup: every capability, writes with confirmation.</summary>
    public static IReadOnlyCollection<string> DefaultDelegations(string provider) =>
        ConnectorCapabilities.Supported(provider);

    /// <summary>Returns a normalized copy or throws <see cref="SettingsValidationException"/>.</summary>
    public static UserSettings Normalize(UserSettings input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = Text(input.DisplayName, 60, "displayName");
        var start = Time(input.WorkStart, "workStart");
        var end = Time(input.WorkEnd, "workEnd");
        if (start is not null && end is not null && string.CompareOrdinal(start, end) >= 0)
            throw new SettingsValidationException("workEnd");
        var tools = input.Tools ?? throw new SettingsValidationException("tools");
        if (tools.Crm is not null && !CrmSystems.Contains(tools.Crm))
            throw new SettingsValidationException("tools.crm");
        if (!Enum.IsDefined(input.AgentMode)) throw new SettingsValidationException("agentMode");
        var accounts = input.Accounts ?? throw new SettingsValidationException("accounts");
        if (accounts.Count > MaxAccounts) throw new SettingsValidationException("accounts");

        var normalized = new List<DeclaredAccount>();
        foreach (var account in accounts)
        {
            if (account is null || account.Id == Guid.Empty || normalized.Any(a => a.Id == account.Id))
                throw new SettingsValidationException("accounts.id");
            if (!ConnectorProviders.IsKnown(account.Provider)) throw new SettingsValidationException("accounts.provider");
            if (!Enum.IsDefined(account.Usage)) throw new SettingsValidationException("accounts.usage");
            var label = Text(account.Label, 60, "accounts.label") ?? throw new SettingsValidationException("accounts.label");
            if (account.ConnectedAccountId is { } connected &&
                (connected == Guid.Empty || normalized.Any(a => a.ConnectedAccountId == connected)))
                throw new SettingsValidationException("accounts.connectedAccountId");
            var supported = ConnectorCapabilities.Supported(account.Provider);
            var delegations = (account.Delegations ?? throw new SettingsValidationException("accounts.delegations"))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (delegations.Any(d => !supported.Contains(d)))
                throw new SettingsValidationException("accounts.delegations");
            normalized.Add(account with
            {
                Label = label,
                Delegations = supported.Where(delegations.Contains).ToArray()
            });
        }
        return input with { DisplayName = name, WorkStart = start, WorkEnd = end, Accounts = normalized };
    }

    /// <summary>Capabilities granted for a connected account; defaults to the provider's set when undeclared.</summary>
    public static IReadOnlySet<string> Granted(UserSettings? settings, Guid connectedAccountId, string provider)
    {
        var declared = settings?.Accounts.FirstOrDefault(a => a.ConnectedAccountId == connectedAccountId);
        var granted = declared?.Delegations ?? DefaultDelegations(provider);
        if (settings?.AgentMode == AgentMode.ReadOnly)
            granted = granted.Where(ConnectorCapabilities.IsRead).ToArray();
        return granted.ToHashSet(StringComparer.Ordinal);
    }

    private static string? Text(string? value, int max, string field)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > max || trimmed.Any(char.IsControl)) throw new SettingsValidationException(field);
        return trimmed;
    }

    private static string? Time(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimePattern().IsMatch(value) ? value : throw new SettingsValidationException(field);
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimePattern();
}
