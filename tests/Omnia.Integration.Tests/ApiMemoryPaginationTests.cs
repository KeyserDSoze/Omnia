using System.Net;
using System.Net.Http.Json;
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
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Real PostgreSQL 17/pgvector via the restricted RLS API role. Fixture auth
/// is confined to TestServer; the separate Playwright suite proves PKCE/JWT.
/// All content synthetic; NEVER print the private response body to CI logs.
/// </summary>
public sealed class ApiMemoryPaginationTests
{
    [Fact]
    public async Task CursorPagesPreserveEqualTimestampRowsWithoutCrossOwnerLeaks()
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
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureTestServices(s => s
                .AddAuthentication(opt =>
                {
                    opt.DefaultAuthenticateScheme = SignedFixture.SchemeName;
                    opt.DefaultChallengeScheme = SignedFixture.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, SignedFixture>(
                    SignedFixture.SchemeName, _ => { }));
        });
        using var http = factory.CreateClient();
        var tenant = "page-tenant-" + Guid.NewGuid().ToString("N");
        var otherTenant = tenant + "-else";
        const string owner = "pagination-owner";
        const string otherOwner = "pagination-other";
        var occurred = DateTimeOffset.UtcNow.AddMinutes(-2);

        async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string url, string subject = owner,
            string? tenantId = null, object? body = null, bool signed = true)
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = body is null ? null : JsonContent.Create(body)
            };
            if (signed)
            {
                request.Headers.Add("X-Test-Tenant", tenantId ?? tenant);
                request.Headers.Add("X-Test-Subject", subject);
            }
            return await http.SendAsync(request);
        }

        using (var grant = await SendAsync(HttpMethod.Post, "/api/v1/consents",
            body: new
            {
                source = "manual", purpose = "memory.capture",
                policyVersion = "privacy-v1", expiresAt = DateTimeOffset.UtcNow.AddDays(3),
                accepted = true
            }))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);

        // Same timestamp is intentional: cursor MUST tie-break on canonical UUID.
        var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < 13; n++)
        {
            using var capture = await SendAsync(HttpMethod.Post, "/api/v1/memories",
                body: new {
                    source = "manual", externalId = "page-" + n,
                    contentType = "text/plain", content = "Synthetic page row " + n,
                    occurredAt = occurred
                });
            Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
            using var payload = JsonDocument.Parse(await capture.Content.ReadAsStreamAsync());
            Assert.True(saved.Add(payload.RootElement.GetProperty("id").GetString()!));
        }
        Assert.Equal(13, saved.Count);

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        string? borrowedCursor = null;
        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var url = "/api/v1/memories/page?limit=5" +
                (cursor is null ? "" : "&before=" + Uri.EscapeDataString(cursor));
            using var response = await SendAsync(HttpMethod.Get, url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // RED was 404
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var root = parsed.RootElement;
            var records = root.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(pageIndex == 2 ? 3 : 5, records.Count);
            foreach (var row in records)
            {
                var id = row.GetProperty("id").GetString()!;
                Assert.True(observed.Add(id), "Duplicate canonical note ID between pages.");
                Assert.Equal("manual", row.GetProperty("source").GetString());
                Assert.StartsWith("Synthetic page row ", row.GetProperty("summary").GetString());
            }
            cursor = root.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("nextCursor").GetString();
            Assert.Equal(pageIndex < 2, cursor is not null);
            if (pageIndex == 0) borrowedCursor = cursor;
        }
        Assert.True(saved.SetEquals(observed));
        Assert.NotNull(borrowedCursor);
        var borrowedPosition = "&before=" + Uri.EscapeDataString(borrowedCursor!);

        // A cursor is not a permission. JWT sub+tenant + FORCE RLS still own
        // every SELECT regardless of the requested cursor.
        using (var foreignOwner = await SendAsync(
            HttpMethod.Get, "/api/v1/memories/page?limit=5" + borrowedPosition,
            subject: otherOwner))
        {
            Assert.Equal(HttpStatusCode.OK, foreignOwner.StatusCode);
            using var json = JsonDocument.Parse(await foreignOwner.Content.ReadAsStreamAsync());
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }
        using (var foreignTenant = await SendAsync(
            HttpMethod.Get, "/api/v1/memories/page?limit=5" + borrowedPosition,
            tenantId: otherTenant))
        {
            Assert.Equal(HttpStatusCode.OK, foreignTenant.StatusCode);
            using var json = JsonDocument.Parse(await foreignTenant.Content.ReadAsStreamAsync());
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }
        using (var noJwt = await SendAsync(
            HttpMethod.Get, "/api/v1/memories/page?limit=5", signed: false))
            Assert.Equal(HttpStatusCode.Unauthorized, noJwt.StatusCode);

        foreach (var bad in new[] {
            "limit=0", "limit=51", "limit=-2",
            "limit=5&before=not-a-cursor",
            "limit=5&before=" + new string('A', 1024),
            "limit=5&before=ab%3D%3D"
        })
        {
            using var rejected = await SendAsync(
                HttpMethod.Get, "/api/v1/memories/page?" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        }
        // Existing collection endpoint and its JSON array stay compatible.
        using (var legacy = await SendAsync(HttpMethod.Get, "/api/v1/memories?limit=5"))
        {
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            using var json = JsonDocument.Parse(await legacy.Content.ReadAsStreamAsync());
            Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
            Assert.Equal(5, json.RootElement.GetArrayLength());
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
        public const string SchemeName = "MemoryPageFixture";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers["X-Test-Tenant"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", subject)],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(claims), SchemeName)));
        }
    }
}
