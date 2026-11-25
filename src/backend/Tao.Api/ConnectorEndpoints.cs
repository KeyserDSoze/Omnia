using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure;
using Tao.Infrastructure.Connectors;
using Tao.Infrastructure.Assistant;

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

public sealed record BriefingCommand(DateTimeOffset? From, DateTimeOffset? To, string? TimeZone);

public sealed record SaveSettingsCommand(string? DisplayName, string? WorkStart, string? WorkEnd, WorkTools? Tools,
    IReadOnlyList<DeclaredAccount>? Accounts, AgentMode? AgentMode, bool CompleteOnboarding);

/// <summary>Social identity providers enabled in Keycloak (TAO_SOCIAL_LOGIN), shown on the sign-in screen.</summary>
public sealed record SocialLoginOptions(IReadOnlyList<string> Providers)
{
    public static SocialLoginOptions Parse(string? value)
    {
        var providers = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (providers.Any(p => !ConnectorProviders.IsKnown(p)))
            throw new InvalidOperationException("Auth:SocialProviders accepts only google and microsoft.");
        return new SocialLoginOptions(providers);
    }
}

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
        services.AddSingleton(SocialLoginOptions.Parse(configuration["Auth:SocialProviders"]));
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
        services.AddScoped<ISettingsStore, PostgresSettingsStore>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ConnectorService>();
        services.AddScoped<TodayService>();

        // Fail fast on an unsafe model endpoint; an empty endpoint simply disables the assistant.
        var llm = configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
        LlmOptions.ValidatedEndpoint(llm.Endpoint);
        services.AddSingleton(llm);
        services.AddHttpClient<OpenAiCompatibleChatModel>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        });
        services.AddTransient<IChatModel>(sp => sp.GetRequiredService<OpenAiCompatibleChatModel>());
        services.AddScoped<DailyBriefingService>();
        return services;
    }

    public static void MapTaoConnectors(this IEndpointRouteBuilder app)
    {
        // Public: needed before sign-in; contains no user data.
        app.MapGet("/api/login-options", (SocialLoginOptions options) => Results.Ok(new { providers = options.Providers }));

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
        today.MapPost("/briefing", (BriefingCommand input, DailyBriefingService service, TimeProvider time,
            ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
        {
            if (!TryTimeZone(input.TimeZone, out var zone))
                return Results.BadRequest(new { error = "invalid_time_zone" });
            var start = input.From ?? time.GetUtcNow().AddHours(-12);
            return Results.Ok(await service.GenerateAsync(tenant, subject, start, input.To ?? start.AddHours(36),
                zone, ct));
        }));
        today.MapGet("/recap", (TodayService service, ClaimsPrincipal user, int? days, string? timeZone,
            CancellationToken ct) => Run(user, async (tenant, subject) =>
            !TryTimeZone(timeZone, out var zone)
                ? Results.BadRequest(new { error = "invalid_time_zone" })
                : Results.Ok(new { days = await service.RecapAsync(tenant, subject, days ?? 3, zone, ct) })));

        // Settings composed by the guided setup: the agent acts on behalf of the user only as declared here.
        var settings = app.MapGroup("/api/v1/settings").RequireAuthorization();
        settings.MapGet("", (SettingsService service, ConnectorService connectors, ClaimsPrincipal user,
            CancellationToken ct) => Run(user, async (tenant, subject) => Results.Ok(new
        {
            settings = await service.GetAsync(tenant, subject, ct),
            providers = connectors.Providers(),
            accounts = await connectors.ListAccountsAsync(tenant, subject, ct)
        })));
        settings.MapPut("", (SaveSettingsCommand input, SettingsService service, ClaimsPrincipal user,
            CancellationToken ct) => Run(user, async (tenant, subject) =>
        {
            if (input.Tools is null || input.Accounts is null || input.AgentMode is null)
                return Results.BadRequest(new { error = "invalid_settings" });
            var saved = await service.SaveAsync(tenant, subject, new UserSettings(input.DisplayName, input.WorkStart,
                input.WorkEnd, input.Tools, input.Accounts, input.AgentMode.Value), input.CompleteOnboarding, ct);
            return Results.Ok(saved);
        }));

        app.MapGet("/api/v1/assistant", (IChatModel model, LlmOptions options) => Results.Ok(new
        {
            configured = model.IsConfigured,
            model = model.IsConfigured ? model.Model : null,
            host = model.IsConfigured ? LlmOptions.ValidatedEndpoint(options.Endpoint)?.Host : null
        })).RequireAuthorization();
    }

    private static bool TryTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id)) return true;
        return id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') &&
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone!);
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
        catch (AssistantNotConfiguredException)
        {
            return Results.Json(new { error = "assistant_not_configured" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ConsentRequiredException)
        {
            return Results.Json(new { error = "assistant_consent_required" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AssistantUnavailableException e)
        {
            return Results.Json(new { error = e.Code }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (DelegationRequiredException)
        {
            return Results.Json(new { error = "delegation_required" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SettingsValidationException e)
        {
            return Results.BadRequest(new { error = "invalid_settings", field = e.Field });
        }
        catch (ArgumentException e) when (e.Message is "invalid_window" or "invalid_item" or "invalid_decision"
            or "invalid_snooze" or "apply_not_supported" or "invalid_task")
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }
}
