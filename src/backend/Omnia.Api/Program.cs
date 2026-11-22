using System.Security.Claims;
using System.Text.Json;
using Omnia.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Omnia.Application;
using Omnia.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// Fail before opening the restored DB, running migrations or starting workers.
RecoveryQuarantineGate.Verify(builder.Configuration, builder.Environment.IsDevelopment());
var connection = builder.Configuration["Database:ConnectionString"];
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Database:ConnectionString is required (no in-memory production fallback).");
var migrationConnection = builder.Configuration["Database:MigrationConnectionString"];
if (string.IsNullOrWhiteSpace(migrationConnection))
    throw new InvalidOperationException("Database:MigrationConnectionString is required for schema migrations.");
var retentionConnection = builder.Configuration["Database:RetentionConnectionString"];
if (string.IsNullOrWhiteSpace(retentionConnection))
    throw new InvalidOperationException("Database:RetentionConnectionString is required for automated expiry.");
var retentionUser = new NpgsqlConnectionStringBuilder(retentionConnection).Username;
var runtimeUser = new NpgsqlConnectionStringBuilder(connection).Username;
var migrationUser = new NpgsqlConnectionStringBuilder(migrationConnection).Username;
if (string.IsNullOrWhiteSpace(runtimeUser) || string.IsNullOrWhiteSpace(migrationUser) ||
    string.IsNullOrWhiteSpace(retentionUser) ||
    string.Equals(runtimeUser, migrationUser, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(retentionUser, migrationUser, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(runtimeUser, retentionUser, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Runtime, retention and migration PostgreSQL identities must be distinct.");
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
// The previous unbounded ASP.NET per-actor policy is replaced with a
// process-local fixed window whose tracked identities have an explicit cap.
// Both actor and tenant partitions use only verified OIDC claims upstream.
var actorRequestLimiter = new ApiActorRequestWindowLimiter(
    builder.Configuration.GetValue<int?>("RateLimit:RequestsPerMinute") ??
    ApiActorRequestWindowLimiter.DefaultRequestsPerMinute,
    builder.Configuration.GetValue<int?>("RateLimit:MaxTrackedActors") ??
    ApiActorRequestWindowLimiter.DefaultMaxTrackedActors,
    maxTrackedActorsPerTenant: builder.Configuration.GetValue<int?>(
        "RateLimit:MaxTrackedActorsPerTenant"));
 // Tenant-wide request/minute budget is separate from existing actor policy.
 // Bounded windows are stored only on this process, never across replicas.
var tenantRequestLimiter = new ApiTenantRequestWindowLimiter(
    builder.Configuration.GetValue<int?>("RateLimit:TenantRequestsPerMinute") ??
    ApiTenantRequestWindowLimiter.DefaultTenantRequestsPerMinute,
    builder.Configuration.GetValue<int?>("RateLimit:MaxTrackedTenants") ??
    ApiTenantRequestWindowLimiter.DefaultMaxTrackedTenants);
// Only the joint limiter is wired to live requests. Never consume one
// fixed-window budget before checking the other (non-refundable permits).
var atomicRequestLimiter = new ApiAtomicRequestWindowLimiter(
    actorRequestLimiter, tenantRequestLimiter);
// The Kestrel body-size limit is NOT sufficient for TestServer and
// alternate hosts. Bound JSON mutations in the application as well.
var maxJsonBytes = ApiJsonBodyBudget.ValidateLimit(
    builder.Configuration.GetValue<int?>("RequestBody:MaxJsonBytes") ??
    ApiJsonBodyBudget.DefaultMaxJsonBytes);
// Total time available only for receiving the authenticated JSON body.
var jsonBodyReadTimeoutMs = ApiJsonBodyBudget.ValidateReadTimeoutMilliseconds(
    builder.Configuration.GetValue<int?>("RequestBody:ReadTimeoutMilliseconds") ??
    ApiJsonBodyBudget.DefaultReadTimeoutMilliseconds);
// One shared, process-local cap for concurrently receiving JSON bodies.
// The permit never spans downstream handler execution; this is not an
// ingress connection cap or a distributed SaaS quota.
var jsonBodyReadLimiter = new ApiJsonBodyReadConcurrencyLimiter(
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentReads") ??
    ApiJsonBodyReadConcurrencyLimiter.DefaultMaxConcurrentReads,
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentReadsPerActor"),
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentReadsPerTenant"));
// Independently cap middleware-retained JSON buffers WHILE downstream
// handlers run. Read permits still release before handler execution.
var jsonBufferedRequestLimiter = new ApiJsonBufferedRequestLimiter(
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentBufferedRequests") ??
    ApiJsonBufferedRequestLimiter.DefaultMaxConcurrentBufferedRequests,
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentBufferedRequestsPerTenant"),
    builder.Configuration.GetValue<int?>("RequestBody:MaxConcurrentBufferedRequestsPerActor"));
// Validate transport limits BEFORE starting Kestrel or opening any sockets.
var requestHeadersTimeoutMs = builder.Configuration.GetValue<int?>(
    "Transport:RequestHeadersTimeoutMilliseconds") ??
    ApiKestrelTransportLimits.DefaultRequestHeadersTimeoutMilliseconds;
var maxRequestHeadersTotalBytes = builder.Configuration.GetValue<int?>(
    "Transport:MaxRequestHeadersTotalBytes") ??
    ApiKestrelTransportLimits.DefaultMaxRequestHeadersTotalBytes;
var maxRequestHeaderCount = builder.Configuration.GetValue<int?>(
    "Transport:MaxRequestHeaderCount") ??
    ApiKestrelTransportLimits.DefaultMaxRequestHeaderCount;
ApiKestrelTransportLimits.Validate(
    requestHeadersTimeoutMs, maxRequestHeadersTotalBytes, maxRequestHeaderCount);
var allowedOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("browser", policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST", "DELETE");
}));
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("migrations",
    (_, _) => NpgsqlDataSource.Create(migrationConnection));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("retention",
    (_, _) => NpgsqlDataSource.Create(retentionConnection));
builder.Services.AddSingleton(sp => new RecoveryGuardedRetentionCycle(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHostEnvironment>().IsDevelopment()));
builder.Services.AddHostedService(sp => new RetentionSweepService(
    sp.GetRequiredKeyedService<NpgsqlDataSource>("retention"),
    sp.GetRequiredService<ILogger<RetentionSweepService>>(),
    sp.GetRequiredService<RecoveryGuardedRetentionCycle>()));
var keyRingPath = builder.Configuration["Crypto:KeyRingPath"];
if (string.IsNullOrWhiteSpace(keyRingPath))
    throw new InvalidOperationException("Crypto:KeyRingPath is required; unsealed memory writes are forbidden.");
// The key ring is loaded at startup and is NEVER exposed by an HTTP endpoint.
// This filesystem adapter is for development only; production needs Key Vault/OpenBao.
builder.Services.AddSingleton(EnvelopeKeyRing.FromFile(keyRingPath));
builder.Services.AddSingleton<IEnvelopeEncryption>(sp =>
    new AesGcmEnvelopeEncryption(sp.GetRequiredService<EnvelopeKeyRing>()));
builder.Services.AddScoped<PostgresMemoryRepository>();
builder.Services.AddScoped<IMemoryRepository>(sp =>
    sp.GetRequiredService<PostgresMemoryRepository>());
builder.Services.AddScoped<IMemoryPageRepository>(sp =>
    sp.GetRequiredService<PostgresMemoryRepository>());
builder.Services.AddScoped<IMemoryPageCatalog, MemoryPageCatalog>();
builder.Services.AddScoped<PostgresConsentRepository>();
builder.Services.AddScoped<IConsentRepository>(sp => sp.GetRequiredService<PostgresConsentRepository>());
builder.Services.AddScoped<IConsentGuard>(sp => sp.GetRequiredService<PostgresConsentRepository>());
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IConsentService, ConsentService>();
builder.Services.AddScoped<IMemoryCatalog, MemoryCatalog>();
builder.Services.AddScoped<IPersonalDataService, PostgresPersonalDataService>();
builder.Services.AddSingleton(sp => new PostgresMigrator(
    sp.GetRequiredKeyedService<NpgsqlDataSource>("migrations"), runtimeUser, retentionUser,
    builder.Configuration["Database:BackfillRole"],
    builder.Configuration["Database:BackfillAuditRole"]));
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
// Kestrel's header parser runs before middleware. Keep a strict pre-auth
// header budget and its 240 B/s minimum request-body rate; the later
// authenticated JSON middleware still owns 408/413/415/503 decisions.
builder.WebHost.ConfigureKestrel(options =>
    ApiKestrelTransportLimits.Configure(
        options.Limits, requestHeadersTimeoutMs,
        maxRequestHeadersTotalBytes, maxRequestHeaderCount));
var app = builder.Build();
// Every /api/v1 response (including authentication/limit/errors and streaming)
// is personal or security-relevant. Apply no-store BEFORE even recovery
// quarantine; OnStarting overrides downstream header changes.
app.Use(ApiPrivateResponseCachePolicy.InvokeAsync);
// Recheck on each request: an operator can quarantine a running API by
// withdrawing the ready marker. This is not a signed recovery release.
app.Use(async (context, next) =>
{
    if (!RecoveryQuarantineGate.IsReady(builder.Configuration, builder.Environment.IsDevelopment()))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            new { error = "recovery_quarantined" },
            cancellationToken: context.RequestAborted);
        return;
    }
    await next(context);
});
app.UseRouting();
app.UseCors("browser");
app.UseAuthentication();
app.UseAuthorization();
// All keys come from OIDC-validated claims AFTER auth+authorization.
// Probe both request windows, then commit BOTH under one coordination lock.
// No quota/partition is allocated when either quota rejects with 429.
app.Use((context, next) =>
    ApiAtomicRequestRateBudget.InvokeAsync(context, next, atomicRequestLimiter));
// Authentication and rate limiting happen BEFORE buffering any user bytes.
// No credentials or request content are logged by the bounded guard.
app.Use((context, next) =>
    ApiJsonBodyBudget.InvokeAsync(context, next, maxJsonBytes, jsonBodyReadTimeoutMs,
        jsonBodyReadLimiter, jsonBufferedRequestLimiter));
app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "Omnia" }));
app.MapGet("/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ready" });
}).RequireAuthorization();

// First-party user consent: scoped by signed JWT sub + tenant_id; the client never
// submits its own tenant/owner. This does NOT prove consent from third parties.
var consents = app.MapGroup("/api/v1/consents").RequireAuthorization();
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

var memories = app.MapGroup("/api/v1/memories").RequireAuthorization();
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
// Additive keyset pagination; existing array-shaped GET /memories stays
// unchanged for older clients. The cursor only positions a signed/RLS read.
memories.MapGet("/page", async (IMemoryPageCatalog catalog,
    ClaimsPrincipal user, int? limit, string? before, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    try
    {
        var page = await catalog.PageAsync(
            identity.Value.Tenant, identity.Value.Subject, limit, before, ct);
        return Results.Ok(new { page.Items, page.NextCursor });
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_memory_page" });
    }
});
memories.MapGet("", async (IMemoryCatalog catalog, ClaimsPrincipal user, int? limit, CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    return Results.Ok(await catalog.RecentAsync(identity.Value.Tenant, identity.Value.Subject,
        limit ?? 25, ct));
});

var privacy = app.MapGroup("/api/v1/privacy")
    .RequireAuthorization();
privacy.MapGet("/export", (IPersonalDataService service, ClaimsPrincipal user,
    HttpContext context) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.Stream(
        stream => service.WriteExportAsync(identity.Value.Tenant, identity.Value.Subject,
            stream, context.RequestAborted),
        "application/json", "omnia-personal-data.json");
});

privacy.MapPost("/erase", async (EraseMyDataCommand input,
    IPersonalDataService service, ClaimsPrincipal user, HttpContext context,
    CancellationToken ct) =>
{
    var identity = Identity(user);
    if (identity is null) return Results.Forbid();
    if (input.Confirmation != "ERASE_MY_DATA")
        return Results.BadRequest(new { error = "explicit_confirmation_required" });
    var result = await service.EraseAsync(identity.Value.Tenant, identity.Value.Subject, ct);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new
    {
        result.DeletedMemories, result.DeletedConsents, result.CompletedAt,
        scope = "omnia-postgresql-primary",
        backupAndExternalPropagation = "not_included"
    });
});

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PostgresMigrator>().MigrateAsync();
    await PostgresRuntimeGuard.VerifyApiAsync(
        scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
    await PostgresRetentionRepository.VerifyWorkerRoleAsync(
        scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>("retention"));
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

public sealed record EraseMyDataCommand(string Confirmation);
