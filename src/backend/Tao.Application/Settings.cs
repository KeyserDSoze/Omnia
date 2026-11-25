using Tao.Domain;

namespace Tao.Application;

/// <summary>Actor-scoped persistence of the settings composed by the guided setup.</summary>
public interface ISettingsStore
{
    Task<UserSettings?> GetAsync(string tenantId, string ownerSubject, CancellationToken ct);
    Task SaveAsync(string tenantId, string ownerSubject, UserSettings settings, CancellationToken ct);
}

/// <summary>The user did not delegate this action to the agent for that account.</summary>
public sealed class DelegationRequiredException() : Exception("delegation_required");

public sealed class SettingsService(ISettingsStore store, IConnectorStore connectors, TimeProvider time)
{
    public async Task<UserSettings?> GetAsync(string tenantId, string ownerSubject, CancellationToken ct)
    {
        var settings = await store.GetAsync(tenantId, ownerSubject, ct);
        return settings is null ? null : await WithValidLinksAsync(tenantId, ownerSubject, settings, ct);
    }

    /// <summary>
    /// Validates and stores the settings. A declared account can only point to a connector account
    /// of the same actor and provider; anything else is unlinked rather than trusted.
    /// </summary>
    public async Task<UserSettings> SaveAsync(string tenantId, string ownerSubject, UserSettings input,
        bool completeOnboarding, CancellationToken ct)
    {
        var settings = await WithValidLinksAsync(tenantId, ownerSubject, UserSettings.Normalize(input), ct);
        var previous = await store.GetAsync(tenantId, ownerSubject, ct);
        settings = settings with
        {
            OnboardingCompletedAt = previous?.OnboardingCompletedAt ??
                (completeOnboarding ? time.GetUtcNow() : null)
        };
        await store.SaveAsync(tenantId, ownerSubject, settings, ct);
        return settings;
    }

    private async Task<UserSettings> WithValidLinksAsync(string tenantId, string ownerSubject, UserSettings settings,
        CancellationToken ct)
    {
        var owned = await connectors.ListAccountsAsync(tenantId, ownerSubject, ct);
        return settings with
        {
            Accounts = settings.Accounts.Select(a => a.ConnectedAccountId is { } id &&
                !owned.Any(o => o.Id == id && o.Provider == a.Provider) ? a with { ConnectedAccountId = null } : a)
                .ToArray()
        };
    }
}
