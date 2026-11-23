using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// #180 RED: real PostgreSQL pgvector under a restricted FORCE RLS actor role.
/// The only test authentication override is on TestServer. The separate
/// Keycloak/Chromium workflow tests genuine signed bearer tokens.
/// </summary>
public sealed class ApiConsentPaginationTests
{
    [Fact]
    public async Task LedgerPagesReachEveryActiveAndHistoricalGrantWithoutCrossActorLeaks()
    {
        var migration = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtime = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retention = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        var keyRing = Environment.GetEnvironmentVariable("OMNIA_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(migration));
        Assert.False(string.IsNullOrWhiteSpace(runtime));
        Assert.False(string.IsNullOrWhiteSpace(retention));
        Assert.False(string.IsNullOrWhiteSpace(keyRing));

        var cfg = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = runtime,
            ["Database:MigrationConnectionString"] = migration,
            ["Database:RetentionConnectionString"] = retention,
            ["Crypto:KeyRingPath"] = keyRing,
            ["Auth:Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth:Audience"] = "omnia-api",
            ["Embeddings:Provider"] = "none",
            ["RateLimit:RequestsPerMinute"] = "200",
            ["RateLimit:TenantRequestsPerMinute"] = "300"
        };
        using var environment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["Database__ConnectionString"] = runtime,
            ["Database__MigrationConnectionString"] = migration,
            ["Database__RetentionConnectionString"] = retention,
            ["Crypto__KeyRingPath"] = keyRing,
            ["Auth__Issuer"] = cfg["Auth:Issuer"],
            ["Auth__Audience"] = cfg["Auth:Audience"],
            ["RateLimit__RequestsPerMinute"] = "200",
            ["RateLimit__TenantRequestsPerMinute"] = "300"
        });
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = SignedFixture.SchemeName;
                    options.DefaultChallengeScheme = SignedFixture.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, SignedFixture>(
                    SignedFixture.SchemeName, _ => { }));
        });
        using var http = factory.CreateClient();
        var tenant = "consent-page-" + Guid.NewGuid().ToString("N");
        const string owner = "owner-a";
        const string otherOwner = "owner-b";
        var otherTenant = tenant + "-else";

        // A single PostgreSQL statement assigns the exact same granted_at to
        // 275 rows, making UUID tie-break essential. 216 are active; 59 revoked.
        await using (var db = NpgsqlDataSource.Create(runtime!))
        {
            await PostgresRuntimeGuard.VerifyAsync(db);
            async Task Seed(string t, string sub, int count)
            {
                await using var scope = await PostgresActorScope.OpenAsync(db, t, sub, default);
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO consent_grants (id, tenant_id, owner_sub, source,
                        purpose, policy_version, granted_at, expires_at, revoked_at)
                    SELECT gen_random_uuid(), @tenant, @owner,
                        'scope-' || n::text, 'memory.capture', 'privacy-v1',
                        now() - interval '1 hour', now() + interval '2 days',
                        CASE WHEN n > 216 THEN now() - interval '20 minutes'
                             ELSE NULL END
                    FROM generate_series(1, @count) AS n
                    """, scope.Connection, scope.Transaction);
                cmd.Parameters.AddWithValue("tenant", t);
                cmd.Parameters.AddWithValue("owner", sub);
                cmd.Parameters.AddWithValue("count", count);
                Assert.Equal(count, await cmd.ExecuteNonQueryAsync());
                await scope.CommitAsync(default);
            }
            await Seed(tenant, owner, 275);
            await Seed(tenant, otherOwner, 3);
            await Seed(otherTenant, owner, 3);
        }

        async Task<HttpResponseMessage> Get(string route,
            string subject = owner, string? tenantId = null, bool signed = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (signed)
            {
                request.Headers.Add("X-Test-Tenant", tenantId ?? tenant);
                request.Headers.Add("X-Test-Subject", subject);
            }
            return await http.SendAsync(request);
        }

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeCount = 0;
        var revokedCount = 0;
        string? cursor = null;
        string? borrowed = null;
        for (var pageIndex = 0; pageIndex < 15; pageIndex++)
        {
            var route = "/api/v1/consents/page?limit=25" +
                (cursor is null ? "" : "&before=" + Uri.EscapeDataString(cursor));
            using var response = await Get(route);
            // RED: endpoint does not exist yet, HTTP 404. No fake mocks.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 25);
            foreach (var item in items)
            {
                Assert.True(observed.Add(item.GetProperty("id").GetString()!),
                    "A canonical consent id must appear in exactly one page.");
                Assert.Equal("memory.capture", item.GetProperty("purpose").GetString());
                if (item.GetProperty("active").GetBoolean()) activeCount++;
                else
                {
                    Assert.NotEqual(JsonValueKind.Null,
                        item.GetProperty("revokedAt").ValueKind);
                    revokedCount++;
                }
            }
            cursor = json.RootElement.GetProperty("nextCursor").ValueKind ==
                JsonValueKind.Null ? null : json.RootElement.GetProperty("nextCursor").GetString();
            if (pageIndex == 0) borrowed = cursor;
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(275, observed.Count);
        Assert.Equal(216, activeCount);
        Assert.Equal(59, revokedCount);
        Assert.NotNull(borrowed);

        // A borrowed cursor is only a position; another JWT actor still gets
        // ONLY its own FORCE RLS-scoped ledger, never the originating rows.
        foreach (var (foreignOwner, foreignTenant) in new[]
        {
            (otherOwner, tenant), (owner, otherTenant)
        })
        {
            using var first = await Get("/api/v1/consents/page?limit=10",
                foreignOwner, foreignTenant);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStreamAsync());
            Assert.Equal(3, firstJson.RootElement.GetProperty("items").GetArrayLength());

            using var usedCursor = await Get("/api/v1/consents/page?limit=10&before=" +
                Uri.EscapeDataString(borrowed!), foreignOwner, foreignTenant);
            Assert.Equal(HttpStatusCode.OK, usedCursor.StatusCode);
            using var filtered = JsonDocument.Parse(await usedCursor.Content.ReadAsStreamAsync());
            foreach (var item in filtered.RootElement.GetProperty("items").EnumerateArray())
                Assert.DoesNotContain(item.GetProperty("id").GetString(), observed);
        }
        using (var unauth = await Get("/api/v1/consents/page", signed: false))
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        foreach (var bad in new[]
        {
            "limit=0", "limit=-1", "limit=51", "limit=2147483647",
            "limit=25&before=garbage", "limit=25&before=YQ",
            "limit=25&before=" + new string('A', 1024)
        })
        {
            using var result = await Get("/api/v1/consents/page?" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
            using var response = JsonDocument.Parse(await result.Content.ReadAsStreamAsync());
            Assert.Equal("invalid_consent_page",
                response.RootElement.GetProperty("error").GetString());
        }

        // Legacy response remains a bounded ARRAY, not a new page envelope.
        using (var legacy = await Get("/api/v1/consents"))
        {
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            using var json = JsonDocument.Parse(await legacy.Content.ReadAsStreamAsync());
            Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
            Assert.Equal(200, json.RootElement.GetArrayLength());
        }
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _old = [];
        public TemporaryEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _old[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }
        public void Dispose()
        {
            foreach (var (key, value) in _old)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class SignedFixture(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ConsentPageFixture";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", subject)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(claims), SchemeName)));
        }
    }
}
