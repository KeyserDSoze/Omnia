using Xunit;

namespace Tao.Domain.Tests;

public sealed class UserSettingsTests
{
    private static readonly WorkTools Tools = new(true, true, true, false, false, false, "hubspot");

    private static DeclaredAccount Account(string provider = ConnectorProviders.Microsoft, Guid? connected = null,
        params string[] delegations) =>
        new(Guid.NewGuid(), provider, AccountUsage.Work, "  Lavoro ACME ", connected, delegations);

    [Fact]
    public void NormalizesLabelsAndOrdersDelegationsByProviderCatalog()
    {
        var settings = UserSettings.Normalize(new UserSettings(" Ale ", "08:30", "18:00", Tools,
            [Account(delegations: ["chats.read", "calendar.read", "calendar.read"])], AgentMode.ActWithConfirmation));

        Assert.Equal("Ale", settings.DisplayName);
        var account = Assert.Single(settings.Accounts);
        Assert.Equal("Lavoro ACME", account.Label);
        Assert.Equal(["calendar.read", "chats.read"], account.Delegations);
    }

    [Theory]
    [InlineData("google", "chats.read", "accounts.delegations")]
    [InlineData("dropbox", "calendar.read", "accounts.provider")]
    public void RejectsCapabilitiesTheProviderDoesNotHave(string provider, string delegation, string field)
    {
        var error = Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, null, null, Tools, [Account(provider, null, delegation)], AgentMode.ReadOnly)));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void RejectsInvalidTimesCrmLabelsAndDuplicateLinks()
    {
        var linked = Guid.NewGuid();
        Assert.Equal("workStart", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, "25:00", null, Tools, [], AgentMode.ReadOnly))).Field);
        Assert.Equal("workEnd", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, "18:00", "09:00", Tools, [], AgentMode.ReadOnly))).Field);
        Assert.Equal("tools.crm", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, null, null, Tools with { Crm = "sap" }, [], AgentMode.ReadOnly))).Field);
        Assert.Equal("accounts.label", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, null, null, Tools, [Account() with { Label = "x\u0007" }], AgentMode.ReadOnly))).Field);
        Assert.Equal("accounts.connectedAccountId", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, null, null, Tools, [Account(connected: linked), Account(connected: linked)],
                AgentMode.ReadOnly))).Field);
        Assert.Equal("accounts", Assert.Throws<SettingsValidationException>(() => UserSettings.Normalize(
            new UserSettings(null, null, null, Tools, Enumerable.Range(0, 11).Select(_ => Account()).ToArray(),
                AgentMode.ReadOnly))).Field);
    }

    [Fact]
    public void GrantedCapabilitiesFollowDeclarationsAndReadOnlyMode()
    {
        var connected = Guid.NewGuid();
        var settings = new UserSettings(null, null, null, Tools,
            [Account(connected: connected, delegations: ["mail.read", "tasks.complete"])], AgentMode.ActWithConfirmation);

        Assert.Equal(["mail.read", "tasks.complete"], UserSettings.Granted(settings, connected, "microsoft").Order());
        Assert.Equal(["mail.read"], UserSettings.Granted(settings with { AgentMode = AgentMode.ReadOnly }, connected,
            "microsoft"));
        // Accounts connected outside the setup keep the provider defaults until the user decides.
        Assert.Equal(ConnectorCapabilities.Google.Order(), UserSettings.Granted(settings, Guid.NewGuid(), "google").Order());
        Assert.Equal(ConnectorCapabilities.Microsoft.Order(), UserSettings.Granted(null, connected, "microsoft").Order());
    }

    [Fact]
    public void EveryWorkItemKindMapsToAReadCapability()
    {
        foreach (var kind in Enum.GetValues<WorkItemKind>())
            Assert.True(ConnectorCapabilities.IsRead(ConnectorCapabilities.ForKind(kind)));
        Assert.False(ConnectorCapabilities.IsRead(ConnectorCapabilities.TasksComplete));
    }
}
