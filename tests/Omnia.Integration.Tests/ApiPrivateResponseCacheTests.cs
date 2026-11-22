using System.Net;
using System.Security.Claims;
using System.Text;
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

/// <summary>
/// No real user content, fake OIDC claims confined to the TestServer.
/// ASP.NET Core runs its ACTUAL middleware stack and a real non-owner
/// PostgreSQL runtime connection with FORCE RLS; browser OIDC is separate.
/// </summary>
public sealed class ApiPrivateResponseCacheTests
{
    [Fact]
    public async Task PersonalHttpResponsesAreNoStoreIncludingSuccessDenialAndRateLimit()
    {
        var migration = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtime = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retention = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        var keys = Environment.GetEnvironmentVariable("OMNIA_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(migration));
        Assert.False(string.IsNullOrWhiteSpace(runtime));
        Assert.False(string.IsNullOrWhiteSpace(retention));
        Assert.False(string.IsNullOrWhiteSpace(keys));

        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = runtime,
            ["Database:MigrationConnectionString"] = migration,
            ["Database:RetentionConnectionString"] = retention,
            ["Crypto:KeyRingPath"] = keys,
            ["Auth:Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth:Audience"] = "omnia-api",
            ["Embeddings:Provider"] = "none",
            ["RateLimit:RequestsPerMinute"] = "3",
            ["RateLimit:TenantRequestsPerMinute"] = "40"
        };
        using var env = new TestEnvironment(new Dictionary<string, string?>
        {
            ["Database__ConnectionString"] = runtime,
            ["Database__MigrationConnectionString"] = migration,
            ["Database__RetentionConnectionString"] = retention,
            ["Crypto__KeyRingPath"] = keys,
            ["Auth__Issuer"] = settings["Auth:Issuer"],
            ["Auth__Audience"] = settings["Auth:Audience"],
            ["RateLimit__RequestsPerMinute"] = "3",
            ["RateLimit__TenantRequestsPerMinute"] = "40"
        });
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
            b.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = Fixture.SchemeName;
                    options.DefaultChallengeScheme = Fixture.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, Fixture>(
                    Fixture.SchemeName, _ => { }));
        });
        using var client = factory.CreateClient();
        var tenant = "cache-tenant-" + Guid.NewGuid().ToString("N");
        var owner = "cache-owner-" + Guid.NewGuid().ToString("N");

        async Task<HttpResponseMessage> Send(HttpMethod method, string path,
            bool signed = true, HttpContent? body = null, string? actor = null)
        {
            using var req = new HttpRequestMessage(method, path) { Content = body };
            if (signed)
            {
                req.Headers.Add("X-Test-Tenant", tenant);
                req.Headers.Add("X-Test-Subject", actor ?? owner);
            }
            return await client.SendAsync(req);
        }

        using (var grants = await Send(HttpMethod.Get, "/api/v1/consents"))
        {
            Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
            AssertPrivate(grants);
        }
        using (var notes = await Send(HttpMethod.Get, "/api/v1/memories?limit=1"))
        {
            Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
            AssertPrivate(notes);
        }
        using (var challenge = await Send(HttpMethod.Get, "/api/v1/consents", signed: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            AssertPrivate(challenge);
        }
        // Invalid explicit erasure should not hit the database mutator,
        // yet the error response is still private and non-cacheable.
        using (var reject = await Send(HttpMethod.Post, "/api/v1/privacy/erase", body:
            new StringContent("{\"confirmation\":\"NO\"}", Encoding.UTF8, "application/json"),
            actor: "another-owner"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reject.StatusCode);
            AssertPrivate(reject);
        }
        // A real 403: write without the required consent grant.
        using (var denied = await Send(HttpMethod.Post, "/api/v1/memories", body:
            new StringContent(
                "{\"source\":\"manual\",\"externalId\":\"cache-test\",\"contentType\":\"text/plain\",\"content\":\"Synthetic no consent\",\"occurredAt\":\"2026-11-22T10:00:00Z\"}",
                Encoding.UTF8, "application/json"), actor: "no-consent"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            AssertPrivate(denied);
        }
        using (var privacyExport = await Send(HttpMethod.Get, "/api/v1/privacy/export",
            actor: "export-owner"))
        {
            Assert.Equal(HttpStatusCode.OK, privacyExport.StatusCode);
            AssertPrivate(privacyExport);
            // Deliberately never log or inspect the private JSON payload.
        }
        using (var third = await Send(HttpMethod.Get, "/api/v1/consents"))
        {
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            AssertPrivate(third);
        }
        using (var throttled = await Send(HttpMethod.Get, "/api/v1/consents"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
            AssertPrivate(throttled);
        }
        // Public health probe remains independent of the personal-data policy.
        using (var health = await Send(HttpMethod.Get, "/health", signed: false))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Null(health.Headers.CacheControl);
        }
        using (var similar = await Send(HttpMethod.Get, "/api/v1-other", signed: false))
        {
            Assert.Equal(HttpStatusCode.NotFound, similar.StatusCode);
            Assert.Null(similar.Headers.CacheControl);
        }
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
        Assert.Equal("nosniff", Assert.Single(nosniff!));
        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrer));
        Assert.Equal("no-referrer", Assert.Single(referrer!));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();
        public TestEnvironment(Dictionary<string, string?> variables)
        {
            foreach (var (k, value) in variables)
            {
                _previous[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, value);
            }
        }
        public void Dispose()
        {
            foreach (var (key, old) in _previous)
                Environment.SetEnvironmentVariable(key, old);
        }
    }

    private sealed class Fixture(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "CacheTestAuth";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", subject)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(id), SchemeName)));
        }
    }
}
