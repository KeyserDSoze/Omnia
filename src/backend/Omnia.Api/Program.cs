using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Omnia.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Omnia.Application;
using Omnia.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration["Database:ConnectionString"];
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Database:ConnectionString is required (no in-memory production fallback).");
var migrationConnection = builder.Configuration["Database:MigrationConnectionString"];
if (string.IsNullOrWhiteSpace(migrationConnection))
    throw new InvalidOperationException("Database:MigrationConnectionString is required for schema migrations.");
var runtimeUser = new NpgsqlConnectionStringBuilder(connection).Username;
var migrationUser = new NpgsqlConnectionStringBuilder(migrationConnection).Username;
if (string.IsNullOrWhiteSpace(runtimeUser) || string.IsNullOrWhiteSpace(migrationUser) ||
    string.Equals(runtimeUser, migrationUser, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Runtime and migration PostgreSQL identities must be distinct.");
var issuer = builder.Configuration["Auth:Issuer"];
var audience = builder.Configuration["Auth:Audience"];
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var authority) ||
    (authority.Scheme != Uri.UriSchemeHttps && !builder.Environment.IsDevelopment()) ||
    string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("Auth:Issuer (HTTPS outside Development) and Auth:Audience are mandatory.");
var metadata = builder.Configuration["Auth:MetadataAddress"];
if (!string.IsNullOrWhiteSpace(metadata) &&
    (!builder.Environment.IsDevelopment() || !Uri.TryCreate(metadata, UriKind.Absolute, out _)))
    throw new InvalidOperationException("Custom metadata endpoints are allowed only in Development.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = issuer;
        options.Audience = audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        if (!string.IsNullOrWhiteSpace(metadata)) options.MetadataAddress = metadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = issuer,
            ValidateAudience = true, ValidAudience = audience,
            ValidateIssuerSigningKey = true, ValidateLifetime = true,
            RequireSignedTokens = true, RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();
// Per-tenant/user limits are enforced *after* OIDC authentication so a valid
// signed identity cannot escape a quota by changing IP or forged headers.
// Anonymous requests are partitioned by their network peer, not X-Forwarded-For.
var requestsPerMinute = builder.Configuration.GetValue<int?>("RateLimit:RequestsPerMinute") ?? 120;
if (requestsPerMinute is < 1 or > 1000)
    throw new InvalidOperationException("RateLimit:RequestsPerMinute must be between 1 and 1000.");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("omnia-api", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ApiRateLimitPolicy.PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = requestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limited" }, cancellationToken: ct);
    };
});
var allowedOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("browser", policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST", "DELETE");
}));
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("migrations",
    (_, _) => NpgsqlDataSource.Create(migrationConnection));
builder.Services.AddScoped<IMemoryRepository, PostgresMemoryRepository>();
builder.Services.AddScoped<PostgresConsentRepository>();
builder.Services.AddScoped<IConsentRepository>(sp => sp.GetRequiredService<PostgresConsentRepository>());
builder.Services.AddScoped<IConsentGuard>(sp => sp.GetRequiredService<PostgresConsentRepository>());
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IConsentService, ConsentService>();
builder.Services.AddScoped<IMemoryCatalog, MemoryCatalog>();
builder.Services.AddSingleton(sp => new PostgresMigrator(
    sp.GetRequiredKeyedService<NpgsqlDataSource>("migrations"), runtimeUser));
var provider = (builder.Configuration["Embeddings:Provider"] ?? "none").ToLowerInvariant();
var model = builder.Configuration["Embeddings:Model"] ??
    (provider == "ollama" ? "nomic-embed-text" : "text-embedding-3-small");
builder.Services.AddSingleton<IConfigurationAdapter>(
    new EmbeddingSettings(model, builder.Configuration["Embeddings:ApiKey"]));
switch (provider)
{
    case "none": builder.Services.AddSingleton<IEmbeddingProvider, NoEmbeddingProvider>(); break;
    case "ollama":
    case "azure":
        var endpoint = builder.Configuration["Embeddings:Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (provider == "azure" && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("A valid Embeddings:Endpoint is required.");
        if (provider == "azure" && string.IsNullOrWhiteSpace(builder.Configuration["Embeddings:ApiKey"]))
            throw new InvalidOperationException("Embeddings:ApiKey is required in Azure mode.");
        if (provider == "ollama" && !builder.Environment.IsDevelopment() &&
            baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("HTTP Ollama endpoint is Development-only.");
        if (provider == "ollama")
            builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>(
                client => client.BaseAddress = baseUri);
        else
            builder.Services.AddHttpClient<IEmbeddingProvider, AzureOpenAiEmbeddingProvider>(
                client => client.BaseAddress = baseUri);
        break;
    default: throw new InvalidOperationException($"Unsupported Embeddings:Provider: {provider}");
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 70 * 1024);
var app = builder.Build();
app.UseRouting();
app.UseCors("browser");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "Omnia" }));
app.MapGet("/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ready" });
}).RequireAuthorization();

// First-party user consent: scoped by signed JWT sub + tenant_id; the client never
// submits its own tenant/owner. This does NOT prove consent from third parties.
var consents = app.MapGroup("/api/v1/consents").RequireAuthorization().RequireRateLimiting("omnia-api");
consents.MapPost("", async (GrantConsentCommand input, IConsentService service,
    ClaimsPrincipal user, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    try
    {
        var grant = await service.GrantAsync(identity.Value.Tenant, identity.Value.Subject,
            input, ct);
        return Results.Created($"/api/v1/consents/{grant.Id}", new
        {
            grant.Id, grant.Source, grant.Purpose, grant.PolicyVersion,
            grant.GrantedAt, grant.ExpiresAt, grant.RevokedAt
        });
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_consent" }); }
});
consents.MapGet("", async (IConsentService service, TimeProvider time,
    ClaimsPrincipal user, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    var grants = await service.ListAsync(identity.Value.Tenant, identity.Value.Subject, ct);
    var now = time.GetUtcNow();
    return Results.Ok(grants.Select(grant => new
    {
        grant.Id, grant.Source, grant.Purpose, grant.PolicyVersion,
        grant.GrantedAt, grant.ExpiresAt, grant.RevokedAt,
        active = grant.IsActiveAt(now)
    }));
});
consents.MapDelete("/{id:guid}", async (Guid id, IConsentService service,
    ClaimsPrincipal user, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    return await service.RevokeAsync(identity.Value.Tenant, identity.Value.Subject, id, ct)
        ? Results.NoContent() : Results.NotFound();
});

var memories = app.MapGroup("/api/v1/memories").RequireAuthorization().RequireRateLimiting("omnia-api");
memories.MapPost("", async (CaptureMemoryCommand input, IMemoryCatalog catalog,
    ClaimsPrincipal user, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    try
    {
        var item = await catalog.CaptureAsync(identity.Value.Tenant, identity.Value.Subject, input, ct);
        return Results.Ok(new { item.Id, item.Source, item.Summary, item.Tags,
            item.OccurredAt, hasEmbedding = item.Embedding is not null });
    }
    catch (ConsentRequiredException)
    {
        return Results.Json(new { error = "consent_required" }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON content." }); }
});
memories.MapGet("", async (IMemoryCatalog catalog, ClaimsPrincipal user, int? limit, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    return Results.Ok(await catalog.RecentAsync(identity.Value.Tenant, identity.Value.Subject,
        limit ?? 25, ct));
});
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PostgresMigrator>().MigrateAsync();
    await PostgresRuntimeGuard.VerifyAsync(
        scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
}
await app.RunAsync();

static (string Tenant, string Subject)? Identity(ClaimsPrincipal principal)
{
    var subjects = principal.FindAll("sub").Select(c => c.Value).ToArray();
    var tenants = principal.FindAll("tenant_id").Select(c => c.Value).ToArray();
    if (subjects.Length != 1 || tenants.Length != 1 ||
        string.IsNullOrWhiteSpace(subjects[0]) || string.IsNullOrWhiteSpace(tenants[0]) ||
        subjects[0].Length > 255 || tenants[0].Length > 128)
        return null;
    return (tenants[0], subjects[0]);
}

public partial class Program { }
