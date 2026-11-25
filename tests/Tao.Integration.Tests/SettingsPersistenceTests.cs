using System.Text;
using System.Text.Json;
using Npgsql;
using Tao.Domain;
using Tao.Infrastructure;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>Guided-setup settings on real PostgreSQL: sealed, actor-scoped, exported and erased.</summary>
public sealed class SettingsPersistenceTests
{
    [Fact]
    public async Task SettingsAreSealedActorScopedExportedAndErased()
    {
        var adminCs = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "tao_app").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await PostgresRuntimeGuard.VerifyAsync(app);

        var crypto = TestEnvelopeCrypto.Create();
        var store = new PostgresSettingsStore(app, crypto);
        var tenant = "settings-" + Guid.NewGuid().ToString("N");
        var settings = UserSettings.Normalize(new UserSettings("Alessandro", "08:30", "18:00",
            new WorkTools(true, true, false, false, false, false, "dynamics365"),
            [new DeclaredAccount(Guid.NewGuid(), "microsoft", AccountUsage.Work, "Contoso segreto", null,
                ["calendar.read", "mail.read"])], AgentMode.ReadOnly) with { OnboardingCompletedAt = DateTimeOffset.UtcNow });

        Assert.Null(await store.GetAsync(tenant, "alice", default));
        await store.SaveAsync(tenant, "alice", settings, default);
        await store.SaveAsync(tenant, "alice", settings with { DisplayName = "Ale" }, default);

        var loaded = await store.GetAsync(tenant, "alice", default);
        Assert.Equal("Ale", loaded!.DisplayName);
        Assert.Equal(["calendar.read", "mail.read"], Assert.Single(loaded.Accounts).Delegations);
        Assert.Equal(AgentMode.ReadOnly, loaded.AgentMode);
        Assert.Null(await store.GetAsync(tenant, "bob", default));

        await using (var raw = admin.CreateCommand(
            "SELECT encode(sealed_settings, 'escape') FROM user_settings WHERE tenant_id = @t"))
        {
            raw.Parameters.AddWithValue("t", tenant);
            var stored = (string)(await raw.ExecuteScalarAsync())!;
            Assert.DoesNotContain("Contoso", stored);
            Assert.DoesNotContain("dynamics365", stored);
        }

        var data = new PostgresPersonalDataService(app, crypto);
        await using (var output = new MemoryStream())
        {
            await data.WriteExportAsync(tenant, "alice", output, default);
            using var json = JsonDocument.Parse(output.ToArray());
            var exported = json.RootElement.GetProperty("settings");
            Assert.Equal("Ale", exported.GetProperty("displayName").GetString());
            Assert.Equal("read_only", exported.GetProperty("agentMode").GetString());
            Assert.Equal("Contoso segreto", exported.GetProperty("accounts")[0].GetProperty("label").GetString());
        }
        await using (var output = new MemoryStream())
        {
            await data.WriteExportAsync(tenant, "bob", output, default);
            Assert.Contains("\"settings\":null", Encoding.UTF8.GetString(output.ToArray()));
        }

        Assert.Equal(1, (await data.EraseAsync(tenant, "alice", default)).DeletedWorkState);
        Assert.Null(await store.GetAsync(tenant, "alice", default));
    }
}
