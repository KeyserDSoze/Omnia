using Tao.Domain;

namespace Tao.Api;

/// <summary>The catalog of what Tao can connect to (ADR 0111). Static data: no provider call, no secret.</summary>
public static class IntegrationEndpoints
{
    public static void MapTaoIntegrations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/catalog", () => Results.Ok(new
        {
            entries = IntegrationCatalog.Entries.Select(e => new
            {
                id = e.Id,
                kind = Kind(e.Kind),
                direction = e.Direction.ToString().ToLowerInvariant(),
                auth = Auth(e.Auth),
                status = e.Status.ToString().ToLowerInvariant(),
                capabilities = e.Capabilities,
                onPremOnly = e.OnPremOnly
            })
        })).RequireAuthorization();
    }

    private static string Kind(IntegrationKind kind) => kind switch
    {
        IntegrationKind.BuiltIn => "built_in",
        IntegrationKind.Mcp => "mcp",
        _ => "api"
    };

    private static string Auth(IntegrationAuth auth) => auth switch
    {
        IntegrationAuth.ApiKey => "api_key",
        IntegrationAuth.OAuth => "oauth",
        _ => auth.ToString().ToLowerInvariant()
    };
}
