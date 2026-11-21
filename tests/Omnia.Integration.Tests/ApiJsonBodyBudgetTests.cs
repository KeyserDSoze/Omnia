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
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Tests the API request-body anti-abuse boundary with REAL PostgreSQL
/// and TestServer fake auth (not a substitute for real OIDC/JWKS E2E).
/// No personal contents are written to the database by these cases.
/// </summary>
public sealed class ApiJsonBodyBudgetTests
{
    [Fact]
    public async Task AuthenticatedJsonPostFails413ForDeclaredAndChunkedOversizeButPreservesSmallBody()
    {
        var migration = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtime = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retention = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        var ring = Environment.GetEnvironmentVariable("OMNIA_TEST_KEYRING_FILE");
        Assert.False(string.IsNullOrWhiteSpace(migration));
        Assert.False(string.IsNullOrWhiteSpace(runtime));
        Assert.False(string.IsNullOrWhiteSpace(retention));
        Assert.False(string.IsNullOrWhiteSpace(ring));

        using var env = new TestEnvironment(new Dictionary<string, string?>
        {
            ["Database__ConnectionString"] = runtime,
            ["Database__MigrationConnectionString"] = migration,
            ["Database__RetentionConnectionString"] = retention,
            ["Crypto__KeyRingPath"] = ring,
            ["Auth__Issuer"] = "http://localhost:8081/realms/omnia",
            ["Auth__Audience"] = "omnia-api",
            ["RateLimit__RequestsPerMinute"] = "50",
            ["RequestBody__MaxJsonBytes"] = "1024"
        });
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = runtime,
                    ["Database:MigrationConnectionString"] = migration,
                    ["Database:RetentionConnectionString"] = retention,
                    ["Crypto:KeyRingPath"] = ring,
                    ["Auth:Issuer"] = "http://localhost:8081/realms/omnia",
                    ["Auth:Audience"] = "omnia-api",
                    ["Embeddings:Provider"] = "none",
                    ["RateLimit:RequestsPerMinute"] = "50",
                    ["RequestBody:MaxJsonBytes"] = "1024"
                }));
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

        // Declared length: early rejection BEFORE the JSON binder,
        // database transaction, consent handling or AI embedding.
        using (var request = Post(new StringContent(
            "{\"confirmation\":\"" + new string('A', 2048) + "\"}",
            Encoding.UTF8, "application/json")))
        using (var result = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.StatusCode);
            Assert.Contains("request_body_too_large",
                await result.Content.ReadAsStringAsync());
            Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
        }

        // Streamed, untrusted Content-Length absent: must STILL stop at
        // 1024 bytes, independently of Kestrel host request size limits.
        using (var request = Post(new StreamContent(new MemoryStream(
            Encoding.UTF8.GetBytes(new string('X', 2048))))))
        {
            request.Headers.TransferEncodingChunked = true;
            request.Content!.Headers.ContentLength = null;
            using var result = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.StatusCode);
            Assert.Contains("request_body_too_large",
                await result.Content.ReadAsStringAsync());
        }

        // The service does not decompress and MUST reject encodings rather
        // than accepting a compressed bomb or misbinding raw bytes.
        using (var content = new StringContent(
            "{\"confirmation\":\"NO\"}", Encoding.UTF8, "application/json"))
        {
            content.Headers.ContentEncoding.Add("gzip");
            using var request = Post(content);
            using var result = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, result.StatusCode);
            Assert.Contains("unsupported_content_encoding",
                await result.Content.ReadAsStringAsync());
        }

        // Small JSON must remain readable by the normal Minimal API
        // binder; the deliberate wrong confirmation yields its existing
        // normal validation error, not a body-related failure.
        using (var request = Post(new StringContent(
            "{\"confirmation\":\"NO\"}", Encoding.UTF8, "application/json")))
        using (var result = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Contains("explicit_confirmation_required",
                await result.Content.ReadAsStringAsync());
        }

        using (var health = await client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var grants = await GetConsents(client))
            Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
    }

    [Fact]
    public void JsonLimitConfigHasBoundedProductionSafeRange()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateLimit(0));
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateLimit(1023));
        Assert.Throws<InvalidOperationException>(() =>
            ApiJsonBodyBudget.ValidateLimit(65537));
        Assert.Equal(65536, ApiJsonBodyBudget.ValidateLimit(65536));
        Assert.Equal(1024, ApiJsonBodyBudget.ValidateLimit(1024));
    }

    private static HttpRequestMessage Post(HttpContent content)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/privacy/erase")
        {
            Content = content
        };
        request.Headers.Add("X-Test-Tenant", "synthetic-tenant-body");
        request.Headers.Add("X-Test-Subject", "synthetic-owner-body");
        return request;
    }

    private static Task<HttpResponseMessage> GetConsents(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/consents");
        request.Headers.Add("X-Test-Tenant", "synthetic-tenant-body");
        request.Headers.Add("X-Test-Subject", "synthetic-owner-body");
        return client.SendAsync(request);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];
        public TestEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class HeaderTestAuthHandler
        (IOptionsMonitor<AuthenticationSchemeOptions> options,
         ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options, logger, encoder)
    {
        public const string SchemeName = "BodyFixtureAuth";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) ||
                string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", subject)],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
