namespace Tao.Domain;

public enum IntegrationKind { BuiltIn, Mcp, Api }

/// <summary>Source: Tao reads signals. Tool: the assistant may call it. Channel: the user talks to Tao from there.</summary>
public enum IntegrationDirection { Source, Tool, Channel }

/// <summary>How the user authorises the connection.</summary>
public enum IntegrationAuth { None, ApiKey, OAuth, Pairing, Qr }

public enum IntegrationStatus { Available, Planned }

/// <param name="ProviderId">The connector provider behind an available built-in integration.</param>
public sealed record IntegrationEntry(string Id, IntegrationKind Kind, IntegrationDirection Direction,
    IntegrationAuth Auth, IntegrationStatus Status, IReadOnlyList<string> Capabilities,
    string? ProviderId = null, bool OnPremOnly = false);

/// <summary>
/// Everything Tao knows how to connect to, available or planned (ADR 0111). It is the single
/// source for the chat answer to "what can I connect?" and for the settings list.
/// </summary>
public static class IntegrationCatalog
{
    public static readonly IReadOnlyList<IntegrationEntry> Entries =
    [
        new("microsoft-365", IntegrationKind.BuiltIn, IntegrationDirection.Source, IntegrationAuth.OAuth,
            IntegrationStatus.Available, ConnectorCapabilities.Microsoft, ConnectorProviders.Microsoft),
        new("google-workspace", IntegrationKind.BuiltIn, IntegrationDirection.Source, IntegrationAuth.OAuth,
            IntegrationStatus.Available, ConnectorCapabilities.Google, ConnectorProviders.Google),
        new("whatsapp-business", IntegrationKind.BuiltIn, IntegrationDirection.Channel, IntegrationAuth.Pairing,
            IntegrationStatus.Planned, []),
        new("whatsapp-linked-device", IntegrationKind.BuiltIn, IntegrationDirection.Source, IntegrationAuth.Qr,
            IntegrationStatus.Planned, [], OnPremOnly: true),
        new("telegram", IntegrationKind.BuiltIn, IntegrationDirection.Channel, IntegrationAuth.Pairing,
            IntegrationStatus.Planned, []),
        new("mcp-open", IntegrationKind.Mcp, IntegrationDirection.Tool, IntegrationAuth.None, IntegrationStatus.Planned, []),
        new("mcp-key", IntegrationKind.Mcp, IntegrationDirection.Tool, IntegrationAuth.ApiKey, IntegrationStatus.Planned, []),
        new("mcp-oauth", IntegrationKind.Mcp, IntegrationDirection.Tool, IntegrationAuth.OAuth, IntegrationStatus.Planned, []),
        new("api-key", IntegrationKind.Api, IntegrationDirection.Tool, IntegrationAuth.ApiKey, IntegrationStatus.Planned, []),
        new("api-oauth", IntegrationKind.Api, IntegrationDirection.Tool, IntegrationAuth.OAuth, IntegrationStatus.Planned, [])
    ];
}
