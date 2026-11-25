using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure;
using Tao.Infrastructure.Connectors;

namespace Tao.Api;

public static class ApiIdentity
{
    /// <summary>Tenant and subject from the validated token only; never from the request.</summary>
    public static (string Tenant, string Subject)? Of(ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").Select(c => c.Value).ToArray();
        var tenants = principal.FindAll("tenant_id").Select(c => c.Value).ToArray();
        if (subjects.Length != 1 || tenants.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0]) || string.IsNullOrWhiteSpace(tenants[0]) ||
            subjects[0].Length > 255 || tenants[0].Length > 128)
            return null;
        return (tenants[0], subjects[0]);
    }
}

public sealed record CompleteConnectorCommand(string? State, string? Code);

public sealed record TriageDecisionCommand(string? Key, string? Decision, int? SnoozeMinutes, bool ApplyToSource);

/// <summary>
/// Work connectors (Microsoft 365, Google Workspace) and the daily deck. OAuth runs as a
/// confidential client on the API: the browser only relays the one-time code and the state,
/// which is bound to the signed actor that started the flow.
/// </summary>
public static class ConnectorEndpoints
{
    public const string CallbackPath = "connections/callback";

    public static Uri? RedirectUri(string? publicBaseUrl, bool development)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl)) return null;
        if (!Uri.TryCreate(publicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            !(baseUri.Scheme == Uri.UriSchemeHttps ||
              (baseUri.Scheme == Uri.UriSchemeHttp && (development || baseUri.IsLoopback))) ||
            baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0 || baseUri.UserInfo.Length > 0)
            throw new InvalidOperationException(
                "Tao:PublicBaseUrl must be an absolute HTTPS URL (HTTP only for loopback or Development).");
        return new Uri(baseUri, CallbackPath);
    }

    public static IServiceCollection AddTaoConnectors(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new ConnectorSettings(
            RedirectUri(configuration["Tao:PublicBaseUrl"], environment.IsDevelopment())));
        services.AddSingleton(configuration.GetSection("Connectors:Microsoft").Get<MicrosoftConnectorOptions>()
            ?? new MicrosoftConnectorOptions());
        services.AddSingleton(configuration.GetSection("Connectors:Google").Get<GoogleConnectorOptions>()
            ?? new GoogleConnectorOptions());
        static void Limits(HttpClient client)
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        }
        services.AddHttpClient<MicrosoftGraphConnector>(Limits);
        services.AddHttpClient<GoogleWorkspaceConnector>(Limits);
        services.AddTransient<IConnectorProvider>(sp => sp.GetRequiredService<MicrosoftGraphConnector>());
        services.AddTransient<IConnectorProvider>(sp => sp.GetRequiredService<GoogleWorkspaceConnector>());
        services.AddScoped<IConnectorStore, PostgresConnectorStore>();
        services.AddScoped<ITriageStore, PostgresTriageStore>();
        services.AddScoped<ConnectorService>();
        services.AddScoped<TodayService>();
        return services;
    }

    public static void MapTaoConnectors(this IEndpointRouteBuilder app)
    {
        var connectors = app.MapGroup("/api/v1/connectors").RequireAuthorization();
        connectors.MapGet("", async (ConnectorService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (ApiIdentity.Of(user) is not { } identity) return Results.Forbid();
            return Results.Ok(new
            {
                providers = service.Providers(),
                accounts = await service.ListAccountsAsync(identity.Tenant, identity.Subject, ct)
            });
        });
        connectors.MapPost("/{provider}/authorize", (string provider, ConnectorService service,
            ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
        {
            if (!ConnectorProviders.IsKnown(provider)) return Results.NotFound();
            var uri = await service.StartAsync(tenant, subject, provider, ct);
            return Results.Ok(new { authorizationUrl = uri.AbsoluteUri });
        }));
        connectors.MapPost("/complete", (CompleteConnectorCommand input, ConnectorService service,
            ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            Results.Ok(await service.CompleteAsync(tenant, subject, input.State, input.Code, ct))));
        connectors.MapDelete("/accounts/{id:guid}", (Guid id, ConnectorService service,
            ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            await service.DisconnectAsync(tenant, subject, id, ct) ? Results.NoContent() : Results.NotFound()));

        var today = app.MapGroup("/api/v1/today").RequireAuthorization();
        today.MapGet("", (TodayService service, TimeProvider time, ClaimsPrincipal user,
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Run(user, async (tenant, subject) =>
        {
            var now = time.GetUtcNow();
            var start = from ?? now.AddHours(-12);
            return Results.Ok(await service.GetAsync(tenant, subject, start, to ?? start.AddHours(36), ct));
        }));
        today.MapPost("/decisions", (TriageDecisionCommand input, TodayService service,
            ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            Results.Ok(await service.DecideAsync(tenant, subject,
                new TriageCommand(input.Key ?? "", input.Decision ?? "", input.SnoozeMinutes, input.ApplyToSource),
                ct))));
    }

    /// <summary>Stable, content-free error codes; provider messages are never echoed.</summary>
    private static async Task<IResult> Run(ClaimsPrincipal user, Func<string, string, Task<IResult>> action)
    {
        if (ApiIdentity.Of(user) is not { } identity) return Results.Forbid();
        try { return await action(identity.Tenant, identity.Subject); }
        catch (ConnectorNotConfiguredException)
        {
            return Results.Json(new { error = "connector_not_configured" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ConnectorAuthorizationException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        catch (ConnectorUnavailableException e)
        {
            return Results.Json(new { error = e.Code }, statusCode: e.Code == "reconnect_required"
                ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "not_found" });
        }
        catch (ArgumentException e) when (e.Message is "invalid_window" or "invalid_item" or "invalid_decision"
            or "invalid_snooze" or "apply_not_supported" or "invalid_task")
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }
}
