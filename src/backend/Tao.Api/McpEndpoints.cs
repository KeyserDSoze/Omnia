using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Tao.Application;

namespace Tao.Api;

/// <summary>
/// Tao's MCP server (ADR 0112): Streamable HTTP, stateless, JSON replies only, POST only. The caller is always the
/// validated bearer token's tenant and subject; the user's <c>mcp.access</c> consent is checked on every tool call.
/// </summary>
public static class McpEndpoints
{
    public const string Path = "/api/v1/mcp";
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    public static IServiceCollection AddTaoMcp(this IServiceCollection services)
    {
        services.AddScoped<IMcpTool, GetTodayTool>();
        services.AddScoped<IMcpTool, GetRecapTool>();
        services.AddScoped<IMcpTool, ListConnectionsTool>();
        services.AddScoped<IMcpTool, ListIntegrationsTool>();
        services.AddScoped<McpServer>();
        return services;
    }

    /// <summary>RFC 9728 hint on 401 so an MCP client can discover the authorization server.</summary>
    public static Task Challenge(JwtBearerChallengeContext context, IConfiguration configuration)
    {
        if (!context.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{MetadataUrl(context.HttpContext, configuration)}\"";
        return Task.CompletedTask;
    }

    public static void MapTaoMcp(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var allowedOrigins = (configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
            .Select(o => o.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        app.MapPost(Path, async (HttpContext http, McpServer server, CancellationToken ct) =>
        {
            if (ApiIdentity.Of(http.User) is not { } identity) return Results.Forbid();
            if (!OriginAllowed(http.Request, allowedOrigins)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct); }
            catch (JsonException) { return Rpc(StatusCodes.Status400BadRequest, -32700, "parse_error"); }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Rpc(StatusCodes.Status400BadRequest, -32600, "invalid_request");
                var reply = await server.HandleAsync(document.RootElement, new McpCaller(identity.Tenant, identity.Subject), ct);
                return reply is null ? Results.Accepted() : Results.Content(reply.ToJsonString(), "application/json");
            }
        }).RequireAuthorization();

        // No server-initiated stream and no session to end.
        app.MapMethods(Path, ["GET", "DELETE"], (HttpContext http) =>
        {
            http.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).RequireAuthorization();

        IResult Metadata(HttpContext http) => Results.Json(new
        {
            resource = ResourceUrl(http, configuration),
            authorization_servers = new[] { configuration["Auth:Issuer"] },
            bearer_methods_supported = new[] { "header" },
            resource_name = "Tao"
        });
        app.MapGet(MetadataPath, Metadata).AllowAnonymous();
        app.MapGet(MetadataPath + Path, Metadata).AllowAnonymous();
    }

    private static IResult Rpc(int status, int code, string message) => Results.Content(
        new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new System.Text.Json.Nodes.JsonObject { ["code"] = code, ["message"] = message }
        }.ToJsonString(), "application/json", statusCode: status);

    /// <summary>DNS-rebinding guard: a browser Origin must be Tao's own host or a configured CORS origin.</summary>
    private static bool OriginAllowed(HttpRequest request, HashSet<string> allowed)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return allowed.Contains(origin.TrimEnd('/')) ||
            string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResourceUrl(HttpContext http, IConfiguration configuration) =>
        configuration["Mcp:PublicUrl"] is { Length: > 0 } configured
            ? configured : $"{http.Request.Scheme}://{http.Request.Host}{Path}";

    private static string MetadataUrl(HttpContext http, IConfiguration configuration) =>
        new Uri(new Uri(ResourceUrl(http, configuration)), MetadataPath + Path).ToString();
}
