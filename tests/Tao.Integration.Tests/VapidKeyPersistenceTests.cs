using Npgsql;
using Tao.Infrastructure;
using Tao.Infrastructure.Push;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// The server's VAPID key is one global row. Test classes that touch it share this collection so they run one
/// after the other: the test envelope key is random per run and a row sealed by another key cannot be opened.
/// </summary>
[CollectionDefinition("push-settings", DisableParallelization = true)]
public sealed class PushSettingsCollection;

[Collection("push-settings")]
public sealed class VapidKeyPersistenceTests
{
    [Fact]
    public async Task TheVapidKeyIsCreatedOnceSealedAndSharedByEveryReplica()
    {
        var adminCs = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "tao_app", "tao_retention").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await using (var reset = admin.CreateCommand("DELETE FROM push_settings"))
            await reset.ExecuteNonQueryAsync();
        var crypto = TestEnvelopeCrypto.Create();
        var first = new VapidKeyProvider(app, crypto);
        var replica = new VapidKeyProvider(app, crypto);

        var keys = await Task.WhenAll(first.GetAsync(default), replica.GetAsync(default), first.GetAsync(default));

        Assert.Single(keys.Distinct());
        Assert.Equal(87, keys[0].Length);
        await using var raw = admin.CreateCommand("SELECT count(*), max(length(sealed_vapid_key)) FROM push_settings");
        await using var reader = await raw.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.GetInt32(1) > 120); // an envelope, not a bare key
    }
}
