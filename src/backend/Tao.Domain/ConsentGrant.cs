using System.Text.RegularExpressions;

namespace Tao.Domain;

/// <summary>
/// A narrowly scoped, revocable first-party authorization record. This does NOT establish
/// consent of third-party participants in calls or messages, nor a GDPR lawful basis by itself.
/// </summary>
public sealed record ConsentGrant
{
    public const string MemoryCapture = "memory.capture";
    /// <summary>Sending minimized daily cards to the configured language model for a briefing.</summary>
    public const string AssistantBriefing = "assistant.briefing";
    /// <summary>The browser's speech services (dictation, online voice), which may send audio or text to the browser vendor.</summary>
    public const string BrowserSpeech = "speech.browser";
    /// <summary>Letting another AI (an MCP client) read the user's day through Tao's MCP server.</summary>
    public const string McpAccess = "mcp.access";
    public Guid Id { get; }
    public string TenantId { get; }
    public string OwnerSubject { get; }
    public string Source { get; }
    public string Purpose { get; }
    public string PolicyVersion { get; }
    public DateTimeOffset GrantedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset? RevokedAt { get; }

    private ConsentGrant(Guid id, string tenantId, string ownerSubject,
        string source, string purpose, string policyVersion, DateTimeOffset grantedAt,
        DateTimeOffset expiresAt, DateTimeOffset? revokedAt)
    {
        Id = id;
        TenantId = tenantId;
        OwnerSubject = ownerSubject;
        Source = source;
        Purpose = purpose;
        PolicyVersion = policyVersion;
        GrantedAt = grantedAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
    }

    public static ConsentGrant Issue(string tenantId, string ownerSubject, string source,
        string purpose, string policyVersion, DateTimeOffset grantedAt, DateTimeOffset expiresAt)
    {
        static string Required(string? value, int max, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max)
                throw new ArgumentException("Missing or oversized consent field.", parameter);
            return value.Trim();
        }

        tenantId = Required(tenantId, 128, nameof(tenantId));
        ownerSubject = Required(ownerSubject, 255, nameof(ownerSubject));
        source = Required(source, 120, nameof(source)).ToLowerInvariant();
        purpose = Required(purpose, 64, nameof(purpose));
        policyVersion = Required(policyVersion, 64, nameof(policyVersion)).ToLowerInvariant();

        if (!Regex.IsMatch(source, "^[a-z][a-z0-9._:-]{0,119}$",
                RegexOptions.CultureInvariant) || source.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid source identifier.", nameof(source));
        if (purpose is not (MemoryCapture or AssistantBriefing or BrowserSpeech or McpAccess))
            throw new ArgumentException("Purpose is not supported by this release.", nameof(purpose));
        if (!Regex.IsMatch(policyVersion, "^[a-z0-9][a-z0-9._-]{0,63}$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid policy version.", nameof(policyVersion));

        var start = grantedAt.ToUniversalTime();
        var end = expiresAt.ToUniversalTime();
        if (end <= start || end > start.AddDays(90))
            throw new ArgumentException("Consent lifetime must be between 0 and 90 days.", nameof(expiresAt));

        return new ConsentGrant(Guid.NewGuid(), tenantId, ownerSubject, source, purpose,
            policyVersion, start, end, null);
    }

    public bool IsActiveAt(DateTimeOffset at) =>
        RevokedAt is null && GrantedAt <= at.ToUniversalTime() && ExpiresAt > at.ToUniversalTime();

    public ConsentGrant Revoke(DateTimeOffset at) =>
        RevokedAt is not null ? this :
        new ConsentGrant(Id, TenantId, OwnerSubject, Source, Purpose, PolicyVersion,
            GrantedAt, ExpiresAt, at.ToUniversalTime());

    // Reconstitution is restricted to the persistence adapter; values are still validated
    // against the same domain policy before they can enter the application layer.
    public static ConsentGrant Restore(Guid id, string tenantId, string ownerSubject,
        string source, string purpose, string policyVersion, DateTimeOffset grantedAt,
        DateTimeOffset expiresAt, DateTimeOffset? revokedAt)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Consent must have a persisted identifier.", nameof(id));
        var valid = Issue(tenantId, ownerSubject, source, purpose, policyVersion,
            grantedAt, expiresAt);
        return new ConsentGrant(id, valid.TenantId, valid.OwnerSubject, valid.Source,
            valid.Purpose, valid.PolicyVersion, valid.GrantedAt, valid.ExpiresAt,
            revokedAt?.ToUniversalTime());
    }
}
