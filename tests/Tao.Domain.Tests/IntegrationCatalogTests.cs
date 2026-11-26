using Xunit;
using Tao.Domain;

namespace Tao.Domain.Tests;

public sealed class IntegrationCatalogTests
{
    [Fact]
    public void EveryAvailableIntegrationHasARegisteredProvider()
    {
        foreach (var entry in IntegrationCatalog.Entries.Where(e => e.Status == IntegrationStatus.Available))
            Assert.True(ConnectorProviders.IsKnown(entry.ProviderId), $"{entry.Id} is available without a provider");
    }

    [Fact]
    public void EveryRegisteredProviderIsInTheCatalogAsAvailable()
    {
        foreach (var provider in ConnectorProviders.All)
            Assert.Contains(IntegrationCatalog.Entries, e => e.ProviderId == provider && e.Status == IntegrationStatus.Available);
    }

    [Fact]
    public void IdsAreUniqueAndStable()
    {
        var ids = IntegrationCatalog.Entries.Select(e => e.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", id));
    }

    [Fact]
    public void TheCatalogCoversTheThreeFamilies()
    {
        Assert.Contains(IntegrationCatalog.Entries, e => e.Kind == IntegrationKind.BuiltIn && e.Id == "microsoft-365");
        Assert.Contains(IntegrationCatalog.Entries, e => e.Kind == IntegrationKind.BuiltIn && e.Id == "google-workspace");
        Assert.Contains(IntegrationCatalog.Entries, e => e.Kind == IntegrationKind.BuiltIn && e.Id.StartsWith("whatsapp"));
        Assert.Equal([IntegrationAuth.None, IntegrationAuth.ApiKey, IntegrationAuth.OAuth],
            IntegrationCatalog.Entries.Where(e => e.Kind == IntegrationKind.Mcp).Select(e => e.Auth).Order().ToArray());
        Assert.Equal([IntegrationAuth.ApiKey, IntegrationAuth.OAuth],
            IntegrationCatalog.Entries.Where(e => e.Kind == IntegrationKind.Api).Select(e => e.Auth).Order().ToArray());
    }

    [Fact]
    public void BuiltInWorkCapabilitiesMatchTheDelegations()
    {
        Assert.Equal(ConnectorCapabilities.Microsoft, IntegrationCatalog.Entries.Single(e => e.Id == "microsoft-365").Capabilities);
        Assert.Equal(ConnectorCapabilities.Google, IntegrationCatalog.Entries.Single(e => e.Id == "google-workspace").Capabilities);
    }

    [Fact]
    public void TheUnofficialWhatsAppModeIsOnPremOnlyAndNeverAvailableYet()
    {
        var linked = IntegrationCatalog.Entries.Single(e => e.Id == "whatsapp-linked-device");
        Assert.True(linked.OnPremOnly);
        Assert.Equal(IntegrationStatus.Planned, linked.Status);
        Assert.Equal(IntegrationDirection.Source, linked.Direction);
        Assert.Equal(IntegrationDirection.Channel, IntegrationCatalog.Entries.Single(e => e.Id == "whatsapp-business").Direction);
    }

    [Fact]
    public void OnlyPlannedEntriesAreOpenToThirdPartyServers()
    {
        // Nothing user-supplied is callable yet: those families stay planned until the outbound guard is wired in.
        Assert.All(IntegrationCatalog.Entries.Where(e => e.Kind is IntegrationKind.Mcp or IntegrationKind.Api),
            e => Assert.Equal(IntegrationStatus.Planned, e.Status));
    }
}
