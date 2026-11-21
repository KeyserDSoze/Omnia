using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Omnia.Integration.Tests;

public sealed class ApiRateLimitTests
{
    [Fact]
    public async Task ApiReturns429PerIdentityWithoutThrottlingOtherUsersOrHealth()
    {
        var database = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        Assert.False(string.IsNullOrWhiteSpace(database), "Disposable PostgreSQL is required.");
        var runtimeDatabase = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(runtimeDatabase),
            "A non-owner PostgreSQL test role is required.");
        var retentionDatabase = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(retentionDatabase),
            "A separate retention PostgreSQL test role is required.");
        var keyRingPath = Environment.GetEnvironmentVariable("OMNIA_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(keyRingPath));
        // Minimal-host Program reads configuration at startup before test-host
        // callbacks; temporary environment values model the same secrets/flags
        // supplied to the production entrypoint, without hardcoding test creds.
        using var runtime = new TestEnvironment(new Dictionary<string, string?>
        {
            ["Database__ConnectionString"] = runtimeDatabase,
            ["Database__MigrationConnectionString"] = database,
            ["Database__RetentionConnectionString"] = retentionDatabase,
            ["Crypto__KeyRingPath"] = keyRingPath,
            ["Auth__Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth__Audience"] = "omnia-api",
            ["RateLimit__RequestsPerMinute"] = "3"
        });
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = runtimeDatabase,
                    ["Database:MigrationConnectionString"] = database,
                    ["Database:RetentionConnectionString"] = retentionDatabase,
                    ["Crypto:KeyRingPath"] = keyRingPath,
                    ["Auth:Issuer"] = "http://localhost:8081/realms/omnia",
                    ["Auth:Audience"] = "omnia-api",
                    ["Embeddings:Provider"] = "none",
                    ["RateLimit:RequestsPerMinute"] = "3"
                }));
            // Fake identity is test-only. Production endpoints continue to validate
            // real OIDC/JWKS JWTs; that separate E2E gate remains issue #17.
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderTestAuthHandler>(
                    HeaderTestAuthHandler.SchemeName, _ => { }));
        });
        using var client = factory.CreateClient();
        var tenant = "tenant-" + Guid.NewGuid().ToString("N");
        var subject = "subject-" + Guid.NewGuid().ToString("N");

        async Task<HttpResponseMessage> GetAsync(string tenantId, string subjectId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/consents");
            request.Headers.Add("X-Test-Tenant", tenantId);
            request.Headers.Add("X-Test-Subject", subjectId);
            return await client.SendAsync(request);
        }

        for (var i = 0; i < 3; i++)
        {
            using var response = await GetAsync(tenant, subject);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var blocked = await GetAsync(tenant, subject))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.Contains("rate_limited", await blocked.Content.ReadAsStringAsync());
        }
        using (var otherSubject = await GetAsync(tenant, "different-owner"))
            Assert.Equal(HttpStatusCode.OK, otherSubject.StatusCode);
        using (var otherTenant = await GetAsync(tenant + "-two", subject))
            Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        using (var health = await client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task MultipleSubjectsOfOneTenantCannotBypassAggregateMinuteQuota()
    {
        var migration = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtimeDb = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retentionDb = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        var keyRing = Environment.GetEnvironmentVariable("OMNIA_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(migration), "Real test PostgreSQL required.");
        Assert.False(string.IsNullOrWhiteSpace(runtimeDb));
        Assert.False(string.IsNullOrWhiteSpace(retentionDb));
        Assert.False(string.IsNullOrWhiteSpace(keyRing));

        // Explicit production-equivalent config is read before test-host
        // callbacks. Fixture authentication is NOT used outside xUnit.
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = runtimeDb,
            ["Database:MigrationConnectionString"] = migration,
            ["Database:RetentionConnectionString"] = retentionDb,
            ["Crypto:KeyRingPath"] = keyRing,
            ["Auth:Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth:Audience"] = "omnia-api",
            ["Embeddings:Provider"] = "none",
            ["RateLimit:RequestsPerMinute"] = "10",
            ["RateLimit:TenantRequestsPerMinute"] = "2"
        };
        using var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["Database__ConnectionString"] = runtimeDb,
            ["Database__MigrationConnectionString"] = migration,
            ["Database__RetentionConnectionString"] = retentionDb,
            ["Crypto__KeyRingPath"] = keyRing,
            ["Auth__Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth__Audience"] = "omnia-api",
            ["RateLimit__RequestsPerMinute"] = "10",
            ["RateLimit__TenantRequestsPerMinute"] = "2"
        });
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderTestAuthHandler>(
                    HeaderTestAuthHandler.SchemeName, _ => { }));
        });
        using var client = factory.CreateClient();
        var tenantA = "tenant-rate-" + Guid.NewGuid().ToString("N");
        var tenantB = "tenant-other-" + Guid.NewGuid().ToString("N");

        async Task<HttpResponseMessage> GetAsync(string tenant, string subject,
            bool spoof = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/consents");
            request.Headers.Add("X-Test-Tenant", tenant);
            request.Headers.Add("X-Test-Subject", subject);
            if (spoof)
            {
                request.Headers.Add("X-Tenant-Id", tenantB);
                request.Headers.Add("X-Forwarded-For", "198.51.100.8");
            }
            return await client.SendAsync(request);
        }

        using (var first = await GetAsync(tenantA, "actor-one"))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var second = await GetAsync(tenantA, "actor-two"))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // All three actor rate partitions have unused capacity (limit 10).
        // This THIRD unique actor must hit the aggregate tenant ceiling.
        using (var blocked = await GetAsync(tenantA, "actor-three", spoof: true))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.Contains("rate_limited", await blocked.Content.ReadAsStringAsync());
            Assert.Equal("no-store", blocked.Headers.CacheControl?.ToString());
            Assert.True(blocked.Headers.TryGetValues("Retry-After", out var values));
            Assert.InRange(int.Parse(Assert.Single(values!)), 1, 60);
            Assert.DoesNotContain(tenantA, await blocked.Content.ReadAsStringAsync());
        }
        using (var otherTenant = await GetAsync(tenantB, "actor-one"))
            Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        using (var health = await client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();

        public TestEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class HeaderTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "FixtureAuth";
        public HeaderTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", subject)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
