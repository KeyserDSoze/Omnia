using System.Security.Claims;
using Npgsql;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure;
using Tao.Infrastructure.Outbound;
using Tao.Infrastructure.Push;

namespace Tao.Api;

public sealed record SubscribeRequest(string? Endpoint, SubscriptionKeys? Keys);
public sealed record SubscriptionKeys(string? P256dh, string? Auth);
public sealed record CreateReminderRequest(string? Text, string? Repeat, string? Time, string? Date, string? TimeZone);
public sealed record EnabledRequest(bool Enabled);

/// <summary>Web Push subscriptions and reminders (ADR 0113). The actor is always the validated token's.</summary>
public static class PushEndpoints
{
    public static IServiceCollection AddTaoPush(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(OutboundClient.Create(new OutboundGuard(new SystemOutboundResolver())));
        services.AddSingleton(sp => new VapidKeyProvider(sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IEnvelopeEncryption>()));
        services.AddSingleton<IVapidPublicKey>(sp => sp.GetRequiredService<VapidKeyProvider>());
        services.AddSingleton(new PushOptions(Subject(configuration)));
        services.AddSingleton<IWebPushSender, WebPushSender>();
        services.AddScoped<IPushSubscriptionStore, PostgresPushSubscriptionStore>();
        services.AddScoped<IReminderStore, PostgresReminderStore>();
        services.AddScoped<IReminderClaims>(sp => new PostgresReminderClaims(
            sp.GetRequiredKeyedService<NpgsqlDataSource>("retention")));
        services.AddScoped<PushService>();
        services.AddScoped<ReminderService>();
        services.AddScoped<ReminderDispatcher>();
        services.AddHostedService<ReminderDispatchService>();
        return services;
    }

    /// <summary>The contact the push services may use (VAPID "sub"): the public address of the app.</summary>
    private static string Subject(IConfiguration configuration)
    {
        if (configuration["Push:Subject"] is { Length: > 0 } configured) return configured;
        return Uri.TryCreate(configuration["Tao:PublicBaseUrl"], UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url.GetLeftPart(UriPartial.Authority) : "mailto:admin@localhost";
    }

    public static void MapTaoPush(this IEndpointRouteBuilder app)
    {
        var push = app.MapGroup("/api/v1/push").RequireAuthorization();
        push.MapGet("/config", (PushService service, CancellationToken ct) => Run(null, async (_, _) =>
            Results.Ok(new { publicKey = await service.PublicKeyAsync(ct) })));
        push.MapGet("/subscriptions", (PushService service, ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            Results.Ok((await service.ListAsync(tenant, subject, ct)).Select(s => new
            {
                id = s.Id, service = new Uri(s.Endpoint).Host, createdAt = s.CreatedAt, failures = s.Failures
            }))));
        push.MapPost("/subscriptions", (SubscribeRequest input, PushService service, ClaimsPrincipal user, CancellationToken ct) =>
            Run(user, async (tenant, subject) =>
            {
                var created = await service.SubscribeAsync(tenant, subject, input.Endpoint, input.Keys?.P256dh, input.Keys?.Auth, ct);
                return Results.Created($"/api/v1/push/subscriptions/{created.Id}", new { id = created.Id });
            }));
        push.MapDelete("/subscriptions/{id:guid}", (Guid id, PushService service, ClaimsPrincipal user, CancellationToken ct) =>
            Run(user, async (tenant, subject) => await service.UnsubscribeAsync(tenant, subject, id, ct)
                ? Results.NoContent() : Results.NotFound(new { error = "not_found" })));
        push.MapPost("/test", (PushService service, ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            Results.Ok(new { delivered = await service.SendTestAsync(tenant, subject, ct) })));

        var reminders = app.MapGroup("/api/v1/reminders").RequireAuthorization();
        reminders.MapGet("", (ReminderService service, ClaimsPrincipal user, CancellationToken ct) => Run(user, async (tenant, subject) =>
            Results.Ok((await service.ListAsync(tenant, subject, ct)).Select(Dto))));
        reminders.MapPost("", (CreateReminderRequest input, ReminderService service, ClaimsPrincipal user, CancellationToken ct) =>
            Run(user, async (tenant, subject) =>
            {
                var created = await service.CreateAsync(tenant, subject, input.Text, input.Repeat, input.Time, input.Date, input.TimeZone, ct);
                return Results.Created($"/api/v1/reminders/{created.Id}", Dto(created));
            }));
        reminders.MapPut("/{id:guid}/enabled", (Guid id, EnabledRequest input, ReminderService service, ClaimsPrincipal user,
            CancellationToken ct) => Run(user, async (tenant, subject) =>
            await service.SetEnabledAsync(tenant, subject, id, input.Enabled, ct)
                ? Results.NoContent() : Results.NotFound(new { error = "not_found" })));
        reminders.MapDelete("/{id:guid}", (Guid id, ReminderService service, ClaimsPrincipal user, CancellationToken ct) =>
            Run(user, async (tenant, subject) => await service.DeleteAsync(tenant, subject, id, ct)
                ? Results.NoContent() : Results.NotFound(new { error = "not_found" })));
    }

    private static object Dto(Reminder r) => new
    {
        id = r.Id, text = r.Text, repeat = r.Repeat.ToString().ToLowerInvariant(), time = r.At.ToString("HH:mm"),
        date = r.Date?.ToString("yyyy-MM-dd"), timeZone = r.TimeZone, enabled = r.Enabled, nextRunAt = r.NextRunAt,
        lastSentAt = r.LastSentAt
    };

    /// <summary>Stable, content-free error codes. A null user is for the one endpoint that needs no identity.</summary>
    private static async Task<IResult> Run(ClaimsPrincipal? user, Func<string, string, Task<IResult>> action)
    {
        var identity = user is null ? ("", "") : ApiIdentity.Of(user) is { } found ? found : default((string, string)?);
        if (identity is null) return Results.Forbid();
        try { return await action(identity.Value.Item1, identity.Value.Item2); }
        catch (ConsentRequiredException)
        {
            return Results.Json(new { error = "notification_consent_required" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (TooManyRemindersException)
        {
            return Results.Json(new { error = "too_many_reminders" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException e) when (e.Message.StartsWith("invalid_", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }
}

/// <summary>Sends due reminders: every replica polls, the claim in the database makes each reminder run once.</summary>
public sealed class ReminderDispatchService(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<ReminderDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Reminders:Dispatch", true)) return;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ReminderDispatcher>().DispatchDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    // No reminder text, address or key ever reaches the logs.
                    logger.LogError("Reminder dispatch failed ({Type}); retry at next interval.", e.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
