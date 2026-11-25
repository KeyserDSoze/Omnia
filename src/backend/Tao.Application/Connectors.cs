using System.Security.Cryptography;
using System.Text;
using Tao.Domain;

namespace Tao.Application;

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes);

public sealed record ConnectedIdentity(string ExternalId, string DisplayName, string? Email);

/// <summary>Sealed at rest (AES-GCM envelope bound to tenant, owner and account id).</summary>
public sealed record ConnectorCredentials(OAuthTokens Tokens, ConnectedIdentity Identity);

public sealed record ConnectorAccountSummary(Guid Id, string Provider, string DisplayName, string? Email,
    DateTimeOffset ConnectedAt, IReadOnlyList<string> Scopes);

public sealed record ConnectorProviderInfo(string Id, string DisplayName, bool Configured,
    IReadOnlyList<string> Capabilities);

public sealed record PendingAuthorization(string Provider, string CodeVerifier, DateTimeOffset ExpiresAt);

public sealed record RemovedConnectorAccount(string Provider, ConnectorCredentials Credentials);

public sealed record ConnectorSettings(Uri? RedirectUri);

public sealed class ConnectorNotConfiguredException(string provider)
    : Exception($"Connector '{provider}' is not configured.");

/// <summary>Unknown, foreign, reused or expired OAuth state; never a provider error.</summary>
public sealed class ConnectorAuthorizationException(string reason) : Exception(reason);

/// <summary>The provider refused or failed; <see cref="Code"/> is safe to show to the user.</summary>
public sealed class ConnectorUnavailableException(string code, Exception? inner = null)
    : Exception(code, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// One external work provider (Microsoft Graph, Google Workspace). Read operations are the
/// default; the only write (completing a task) is executed after explicit user confirmation.
/// </summary>
public interface IConnectorProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }
    IReadOnlyList<string> Capabilities { get; }
    Uri BuildAuthorizationUri(string state, string codeChallenge, Uri redirectUri);
    Task<OAuthTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);
    Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct);
    Task<ConnectedIdentity> GetIdentityAsync(string accessToken, CancellationToken ct);
    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(Guid accountId, ConnectedIdentity identity,
        string accessToken, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task CompleteTaskAsync(string accessToken, string externalTaskId, CancellationToken ct);
    Task RevokeAsync(OAuthTokens tokens, CancellationToken ct);
}

/// <summary>Actor-scoped persistence: every call is limited to the signed tenant and subject.</summary>
public interface IConnectorStore
{
    Task SaveAuthorizationAsync(string tenantId, string ownerSubject, string provider, byte[] stateHash,
        string codeVerifier, DateTimeOffset expiresAt, CancellationToken ct);
    /// <summary>Single use: the pending authorization is deleted when read.</summary>
    Task<PendingAuthorization?> ConsumeAuthorizationAsync(string tenantId, string ownerSubject,
        byte[] stateHash, CancellationToken ct);
    Task<ConnectorAccountSummary> UpsertAccountAsync(string tenantId, string ownerSubject, string provider,
        ConnectorCredentials credentials, CancellationToken ct);
    Task<IReadOnlyList<ConnectorAccountSummary>> ListAccountsAsync(string tenantId, string ownerSubject,
        CancellationToken ct);
    Task<RemovedConnectorAccount?> DeleteAccountAsync(string tenantId, string ownerSubject, Guid accountId,
        CancellationToken ct);
    /// <summary>Locks the account while <paramref name="refresh"/> runs, then persists changed credentials.</summary>
    Task<ConnectorCredentials?> UseCredentialsAsync(string tenantId, string ownerSubject, Guid accountId,
        Func<ConnectorCredentials, CancellationToken, Task<ConnectorCredentials>> refresh, CancellationToken ct);
}

public sealed class ConnectorService(IEnumerable<IConnectorProvider> providers, IConnectorStore store,
    TimeProvider time, ConnectorSettings settings)
{
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private readonly IReadOnlyDictionary<string, IConnectorProvider> _providers =
        providers.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public IReadOnlyList<ConnectorProviderInfo> Providers() =>
        ConnectorProviders.All.Where(_providers.ContainsKey).Select(id => _providers[id])
            .Select(p => new ConnectorProviderInfo(p.Id, p.DisplayName,
                p.IsConfigured && settings.RedirectUri is not null, p.Capabilities))
            .ToArray();

    public async Task<Uri> StartAsync(string tenantId, string ownerSubject, string providerId,
        CancellationToken ct)
    {
        var provider = Configured(providerId);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        await store.SaveAuthorizationAsync(tenantId, ownerSubject, provider.Id, HashState(state), verifier,
            time.GetUtcNow() + AuthorizationLifetime, ct);
        return provider.BuildAuthorizationUri(state, challenge, settings.RedirectUri!);
    }

    public async Task<ConnectorAccountSummary> CompleteAsync(string tenantId, string ownerSubject,
        string? state, string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 128 ||
            string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new ConnectorAuthorizationException("invalid_request");
        // The state is bound to the signed actor that started the flow: a state started
        // by someone else (or replayed) is simply not found under this actor's RLS scope.
        var pending = await store.ConsumeAuthorizationAsync(tenantId, ownerSubject, HashState(state), ct)
            ?? throw new ConnectorAuthorizationException("unknown_state");
        if (pending.ExpiresAt <= time.GetUtcNow())
            throw new ConnectorAuthorizationException("expired_state");
        var provider = Configured(pending.Provider);
        OAuthTokens tokens;
        ConnectedIdentity identity;
        try
        {
            tokens = await provider.ExchangeCodeAsync(code, pending.CodeVerifier, settings.RedirectUri!, ct);
            identity = await provider.GetIdentityAsync(tokens.AccessToken, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ConnectorUnavailableException("authorization_failed", e);
        }
        return await store.UpsertAccountAsync(tenantId, ownerSubject, provider.Id,
            new ConnectorCredentials(tokens, identity), ct);
    }

    public Task<IReadOnlyList<ConnectorAccountSummary>> ListAccountsAsync(string tenantId,
        string ownerSubject, CancellationToken ct) => store.ListAccountsAsync(tenantId, ownerSubject, ct);

    public async Task<bool> DisconnectAsync(string tenantId, string ownerSubject, Guid accountId,
        CancellationToken ct)
    {
        var removed = await store.DeleteAccountAsync(tenantId, ownerSubject, accountId, ct);
        if (removed is null) return false;
        if (_providers.TryGetValue(removed.Provider, out var provider))
        {
            try { await provider.RevokeAsync(removed.Credentials.Tokens, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Best effort: Tao no longer holds the tokens; provider-side consent stays visible
                // in the user's Microsoft/Google account settings.
            }
        }
        return true;
    }

    /// <summary>Returns a usable access token, refreshing it under a row lock when close to expiry.</summary>
    public async Task<(IConnectorProvider Provider, ConnectorCredentials Credentials)> AccessAsync(
        string tenantId, string ownerSubject, ConnectorAccountSummary account, CancellationToken ct)
    {
        var provider = Configured(account.Provider);
        var credentials = await store.UseCredentialsAsync(tenantId, ownerSubject, account.Id,
            async (current, token) =>
            {
                if (current.Tokens.ExpiresAt - RefreshMargin > time.GetUtcNow()) return current;
                if (current.Tokens.RefreshToken is not { Length: > 0 } refreshToken)
                    throw new ConnectorUnavailableException("reconnect_required");
                OAuthTokens refreshed;
                try { refreshed = await provider.RefreshAsync(refreshToken, token); }
                catch (ConnectorUnavailableException) { throw; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Network/transport failure: retry later, the grant itself may still be valid.
                    throw new ConnectorUnavailableException("provider_error", e);
                }
                // Google does not rotate refresh tokens: keep the previous one.
                return current with
                {
                    Tokens = refreshed.RefreshToken is { Length: > 0 } ? refreshed
                        : refreshed with { RefreshToken = refreshToken }
                };
            }, ct) ?? throw new KeyNotFoundException("connector_account_not_found");
        return (provider, credentials);
    }

    private IConnectorProvider Configured(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) && provider.IsConfigured &&
        settings.RedirectUri is not null
            ? provider : throw new ConnectorNotConfiguredException(providerId);

    public static byte[] HashState(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
